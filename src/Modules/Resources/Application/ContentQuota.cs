using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Domain;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Resources.Application;

/// <summary>
/// C2 <see cref="IContentQuota"/> (ARC-006 §4). Every change runs in one transaction that first takes
/// <c>SET LOCAL lock_timeout = '3s'</c> and the owner's advisory lock, so concurrent reservations, commits and
/// releases of one owner are serialised; the conditional UPDATE is the guard that keeps
/// <c>used + reserved &lt;= quota</c>, and the CHECK constraints keep both counters non-negative.
/// Guests (no account) have no ledger: their reservations are synthetic (<see cref="Guid.Empty"/>) and every
/// operation on them is a no-op; their volume is limited by the PD-14 creation counters instead.
/// When called inside an open transaction of the same DbContext (RegisterAsync), it joins that transaction.
/// </summary>
public sealed class ContentQuota(ResourcesDbContext db, IContentPlanProvider plans, TimeProvider time) : IContentQuota
{
    public Task<Result<QuotaReservation>> ReserveAsync(ContentOwner owner, long bytes, CancellationToken cancellationToken) =>
        ReserveForResourceAsync(owner, bytes, null, cancellationToken);

    internal async Task<Result<QuotaReservation>> ReserveForResourceAsync(
        ContentOwner owner, long bytes, Guid? resourceId, CancellationToken ct)
    {
        if (bytes < 0) return Error.Validation("The size cannot be negative.");
        if (owner.UserId is not { } ownerId) return Result<QuotaReservation>.Ok(new(Guid.Empty, owner, bytes));

        var quota = (await plans.GetForUserAsync(ownerId, ct)).Limits.StorageQuotaBytes;
        if (quota is not { } limit) return Error.Validation("This plan has no storage quota configured.");

        return await InOwnerTransactionAsync(ownerId, async () =>
        {
            var now = Now();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO resources.quota_ledger (owner_id, used_bytes, reserved_bytes, updated_at) VALUES ({ownerId}, 0, 0, {now}) ON CONFLICT DO NOTHING", ct);
            var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE resources.quota_ledger SET reserved_bytes = reserved_bytes + {bytes}, updated_at = {now}
                WHERE owner_id = {ownerId} AND used_bytes + reserved_bytes + {bytes} <= {limit}
                """, ct);
            if (updated == 0)
                return Error.Of(ErrorCodes.QuotaExceeded, "Your storage is full. Delete content or upgrade to upload more.");

            var reservationId = Guid.CreateVersion7();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO resources.quota_reservations (id, owner_id, resource_id, bytes, state, created_at)
                VALUES ({reservationId}, {ownerId}, {resourceId}, {bytes}, {(short)ReservationState.Reserved}, {now})
                """, ct);
            return Result<QuotaReservation>.Ok(new(reservationId, owner, bytes));
        }, ct);
    }

    /// <summary>F-9: binds an upload's existing reservation to the resource registered at <c>/complete</c> instead of reserving
    /// again, so the declared bytes are counted exactly once. Verifies state reserved, same owner, <c>bytes = expected</c> and
    /// that no other resource owns it. A repeat for the same resource is a no-op success. Guests have no ledger.</summary>
    internal async Task<Result<Guid>> AdoptReservationAsync(
        Guid reservationId, ContentOwner owner, long expectedBytes, Guid resourceId, CancellationToken ct)
    {
        if (owner.UserId is not { } ownerId) return Result<Guid>.Ok(Guid.Empty);
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE resources.quota_reservations SET resource_id = {resourceId}
            WHERE id = {reservationId} AND owner_id = {ownerId} AND bytes = {expectedBytes}
              AND state = {(short)ReservationState.Reserved} AND (resource_id IS NULL OR resource_id = {resourceId})
            """, ct);
        return rows > 0
            ? Result<Guid>.Ok(reservationId)
            : Error.Conflict("The upload's storage reservation is missing, already used or does not match.");
    }

    public Task<Result<bool>> CommitAsync(Guid reservationId, long actualBytes, CancellationToken cancellationToken) =>
        CommitCoreAsync(reservationId, actualBytes, accumulateUsage: false, cancellationToken);

    /// <inheritdoc />
    public Task<Result<bool>> CommitToResourceAsync(Guid reservationId, long actualBytes, CancellationToken cancellationToken) =>
        CommitCoreAsync(reservationId, actualBytes, accumulateUsage: true, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<QuotaReservation>> ReserveForResourceAsync(Guid resourceId, long bytes, CancellationToken cancellationToken)
    {
        var owner = await db.Resources.AsNoTracking().Where(r => r.Id == resourceId)
            .Select(r => new ContentOwner(r.OwnerId, r.GuestCreatorKey)).FirstOrDefaultAsync(cancellationToken);
        return owner is null ? Error.NotFound("Resource not found.") : await ReserveForResourceAsync(owner, bytes, resourceId, cancellationToken);
    }

    private async Task<Result<bool>> CommitCoreAsync(Guid reservationId, long actualBytes, bool accumulateUsage, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (reservationId == Guid.Empty) return Result<bool>.Ok(true); // guest: no ledger
        if (actualBytes < 0) return Error.Validation("The size cannot be negative.");

        var reservation = await db.QuotaReservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null) return Error.NotFound("Reservation not found.");
        // tus Upload-Length is exact, so more bytes than reserved means a bug: refused (T5 rejects too-large).
        if (actualBytes > reservation.Bytes)
            return Error.Of(ErrorCodes.FileTooLarge, "The stored size is larger than the reserved size.");

        return await InOwnerTransactionAsync(reservation.OwnerId, async () =>
        {
            var now = Now();
            var settled = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE resources.quota_reservations SET state = {(short)ReservationState.Committed}, settled_at = {now}
                WHERE id = {reservationId} AND state = {(short)ReservationState.Reserved}
                """, ct);
            if (settled == 0)
            {
                // Replay of a commit is a no-op; a released reservation cannot be committed.
                var state = await db.QuotaReservations.AsNoTracking().Where(r => r.Id == reservationId)
                    .Select(r => r.State).FirstAsync(ct);
                return state == ReservationState.Committed
                    ? Result<bool>.Ok(false)
                    : Error.Conflict("This reservation was already released.");
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE resources.quota_ledger
                SET reserved_bytes = reserved_bytes - {reservation.Bytes}, used_bytes = used_bytes + {actualBytes}, updated_at = {now}
                WHERE owner_id = {reservation.OwnerId}
                """, ct);
            if (reservation.ResourceId is { } resourceId && accumulateUsage)
                // F-4: a new version adds to the resource's usage row (only while it is still counted).
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO resources.quota_usage (resource_id, owner_id, bytes)
                    VALUES ({resourceId}, {reservation.OwnerId}, {actualBytes})
                    ON CONFLICT (resource_id) DO UPDATE SET bytes = quota_usage.bytes + EXCLUDED.bytes
                    WHERE quota_usage.released_at IS NULL
                    """, ct);
            else if (reservation.ResourceId is { } firstResourceId)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO resources.quota_usage (resource_id, owner_id, bytes)
                    VALUES ({firstResourceId}, {reservation.OwnerId}, {actualBytes}) ON CONFLICT DO NOTHING
                    """, ct);
            return Result<bool>.Ok(true);
        }, ct);
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (reservationId == Guid.Empty) return;
        var reservation = await db.QuotaReservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null) return;

        var result = await InOwnerTransactionAsync(reservation.OwnerId, async () =>
        {
            var now = Now();
            var released = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE resources.quota_reservations SET state = {(short)ReservationState.Released}, settled_at = {now}
                WHERE id = {reservationId} AND state = {(short)ReservationState.Reserved}
                """, ct);
            if (released > 0)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE resources.quota_ledger SET reserved_bytes = reserved_bytes - {reservation.Bytes}, updated_at = {now} WHERE owner_id = {reservation.OwnerId}", ct);
            return Result<bool>.Ok(released > 0);
        }, ct);
        ThrowIfBusy(result);
    }

    public async Task ReleaseUsedAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var usage = await db.QuotaUsage.AsNoTracking().FirstOrDefaultAsync(u => u.ResourceId == resourceId, ct);
        if (usage is null || usage.ReleasedAt is not null) return;

        var result = await InOwnerTransactionAsync(usage.OwnerId, async () =>
        {
            var now = Now();
            var released = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE resources.quota_usage SET released_at = {now} WHERE resource_id = {resourceId} AND released_at IS NULL", ct);
            if (released > 0)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE resources.quota_ledger SET used_bytes = used_bytes - {usage.Bytes}, updated_at = {now} WHERE owner_id = {usage.OwnerId}", ct);
            return Result<bool>.Ok(released > 0);
        }, ct);
        ThrowIfBusy(result);
    }

    public async Task<StorageUsage> GetUsageAsync(Guid userId, CancellationToken cancellationToken)
    {
        var ledger = await db.QuotaLedger.AsNoTracking().FirstOrDefaultAsync(l => l.OwnerId == userId, cancellationToken);
        var quota = (await plans.GetForUserAsync(userId, cancellationToken)).Limits.StorageQuotaBytes;
        return new StorageUsage(ledger?.UsedBytes ?? 0, ledger?.ReservedBytes ?? 0, quota);
    }

    /// <summary>
    /// BE-021 (ARC-019 section 9.2, daily 03:00 UTC): the ledger must equal its parts, <c>used = sum(quota_usage not released)</c> and
    /// <c>reserved = sum(open reservations)</c>. Each owner is checked and repaired under the same advisory lock as every other ledger
    /// change, so a reservation in flight cannot be miscounted. Drift is logged by the caller and fixed here; a repair never fails an owner
    /// for the others.
    /// </summary>
    public async Task<QuotaReconciliation> ReconcileAsync(CancellationToken ct)
    {
        var owners = await db.QuotaLedger.AsNoTracking().Select(l => l.OwnerId).ToListAsync(ct);
        int drifted = 0;
        long usedDrift = 0, reservedDrift = 0;
        foreach (var ownerId in owners)
        {
            var drift = await InOwnerTransactionAsync(ownerId, async () =>
            {
                var used = await db.QuotaUsage.Where(u => u.OwnerId == ownerId && u.ReleasedAt == null).SumAsync(u => u.Bytes, ct);
                var reserved = await db.QuotaReservations.Where(r => r.OwnerId == ownerId && r.State == ReservationState.Reserved).SumAsync(r => r.Bytes, ct);
                var ledger = await db.QuotaLedger.AsNoTracking().FirstAsync(l => l.OwnerId == ownerId, ct);
                if (ledger.UsedBytes == used && ledger.ReservedBytes == reserved) return Result<(long, long)>.Ok((0, 0));
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE resources.quota_ledger SET used_bytes = {used}, reserved_bytes = {reserved}, updated_at = {Now()} WHERE owner_id = {ownerId}", ct);
                return Result<(long, long)>.Ok((used - ledger.UsedBytes, reserved - ledger.ReservedBytes));
            }, ct);
            if (!drift.IsSuccess) continue;
            if (drift.Value is (0, 0)) continue;
            drifted++;
            usedDrift += drift.Value.Item1;
            reservedDrift += drift.Value.Item2;
        }
        return new QuotaReconciliation(owners.Count, drifted, usedDrift, reservedDrift);
    }

    // ---- internals ------------------------------------------------------------

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    /// <summary>Runs <paramref name="body"/> under the owner's advisory lock, in the current transaction when one is
    /// open (it is then committed or rolled back by its owner) or in a new one, committed only on success.
    /// A lock timeout (55P03) becomes <c>service_unavailable</c> (503, retry), spec §7.0.</summary>
    private async Task<Result<T>> InOwnerTransactionAsync<T>(Guid ownerId, Func<Task<Result<T>>> body, CancellationToken ct)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended('zaagl.quota:' || {ownerId.ToString()}, 0))", ct);
            var result = await body();
            if (tx is not null)
            {
                if (result.IsSuccess) await tx.CommitAsync(ct);
                else await tx.RollbackAsync(ct);
            }
            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            return Error.Of(ErrorCodes.Busy, "Storage accounting is busy. Try again in a second.");
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private static void ThrowIfBusy(Result<bool> result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
    }
}

/// <summary>Result of one <see cref="ContentQuota.ReconcileAsync"/> pass.</summary>
public sealed record QuotaReconciliation(int Owners, int Drifted, long UsedDriftBytes, long ReservedDriftBytes);
