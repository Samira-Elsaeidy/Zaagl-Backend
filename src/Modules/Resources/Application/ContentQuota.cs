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

    public async Task<Result<bool>> CommitAsync(Guid reservationId, long actualBytes, CancellationToken cancellationToken)
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
            if (reservation.ResourceId is { } resourceId)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO resources.quota_usage (resource_id, owner_id, bytes)
                    VALUES ({resourceId}, {reservation.OwnerId}, {actualBytes}) ON CONFLICT DO NOTHING
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
