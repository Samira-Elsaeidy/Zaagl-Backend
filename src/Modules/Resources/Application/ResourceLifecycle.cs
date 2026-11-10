using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Domain;
using Zaagl.Modules.Resources.Infrastructure;

namespace Zaagl.Modules.Resources.Application;

/// <summary>
/// BE-018 to BE-020 (ARC-019 sections 7 and 8, ARC-006 T11 to T21): everything that changes a resource's bytes after it exists.
/// <list type="bullet">
/// <item>the C10 handler (<see cref="ILinkLifecycleHandler"/>): renewal and delete copies, guest expiry, purge, takedown;</item>
/// <item>the sweeps the worker runs: burn finaliser, purge (account purge_after, guest expiry = purge_after), end of a legal hold,
/// orphans, grant cleanup, tombstones.</item>
/// </list>
/// The link owns the clock; the sweeps read only the resource's copies (<c>link_purge_after</c>), never for state. Every reaction locks
/// the row (<c>FOR UPDATE</c>), re-checks its guard under the lock and is idempotent: the byte deletion is a deduplicated task and the
/// quota release is idempotent per resource, so a job that dies half way is simply run again. Resources cannot reference Files, so
/// bytes are reached through <see cref="IResourceBytes"/>.
/// </summary>
public sealed class ResourceLifecycle(
    ResourcesDbContext db,
    IContentQuota quota,
    IResourceBytes bytes,
    IEnumerable<IResourceKindHandler> kindHandlers,
    IOptions<ResourcesOptions> resourceOptions,
    IOptions<BurnOptions> burn,
    TimeProvider time,
    ILogger<ResourceLifecycle> logger) : ILinkLifecycleHandler
{
    private const int Batch = 200;
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan TombstoneAge = TimeSpan.FromDays(30);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------------------------------------
    // C10 handler (at least once; every reaction is guarded by the current row)
    // ------------------------------------------------------------------------------------------------

    public async Task HandleAsync(LinkLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var id = lifecycleEvent.ResourceId;
        switch (lifecycleEvent)
        {
            case LinkRenewed renewed:
                // T14: only forward, never for a burned or deleted resource.
                await db.Resources
                    .Where(r => r.Id == id && r.BurnClaimedAt == null && r.DeletedAt == null && r.LinkExpiresAt < renewed.ExpiresAt)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.LinkExpiresAt, renewed.ExpiresAt)
                        .SetProperty(r => r.LinkPurgeAfter, renewed.PurgeAfter)
                        .SetProperty(r => r.UpdatedAt, Now), ct);
                break;

            case LinkDeleted deleted:
            {
                // T15: unreachable at once; the bytes and the quota stay until the (shortened) purge_after (PD-55).
                var cap = deleted.OccurredAt.AddDays(Math.Max(0, resourceOptions.Value.QuarantineDays));
                await db.Resources.Where(r => r.Id == id && r.DeletedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.DeletedAt, deleted.OccurredAt)
                        .SetProperty(r => r.LinkPurgeAfter, r => r.LinkPurgeAfter < cap ? r.LinkPurgeAfter : cap)
                        .SetProperty(r => r.UpdatedAt, Now), ct);
                break;
            }

            case LinkExpired { IsGuest: true } expired:
                // T13: a guest's link is purgeable at expiry; the bytes go now. A renewed link (copy later than the event) is left alone.
                await ReclaimAsync(id, "expiry", r => r.LinkExpiresAt <= expired.OccurredAt && r.OwnerId == null, ct);
                break;

            case LinkDisabled { DisabledKind: "abuse" or "safety" }:
                await QuarantineAsync(id, ct); // T16
                break;

            case LinkPurged:
                await ReclaimAsync(id, "purge", _ => true, ct); // T18: final
                break;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Burn finaliser (T11)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Resources claimed and past their grace (or whose claiming grant used all its requests) lose their bytes and their quota.
    /// The request path enforces the grace itself; this only reclaims space. Idempotent; safe to run from several workers.</summary>
    public async Task<int> FinaliseBurnsAsync(CancellationToken ct)
    {
        var now = Now;
        var cutoff = now.AddMinutes(-Math.Max(1, burn.Value.GraceMinutes));
        var max = Math.Max(1, burn.Value.MaxRequests);
        var ids = await db.Resources.AsNoTracking()
            .Where(r => r.BurnClaimedAt != null && r.BytesState == BytesState.Stored
                && (r.BurnClaimedAt <= cutoff || db.DownloadGrants.Any(g => g.Id == r.BurnGrantId && g.RequestCount >= max)))
            .OrderBy(r => r.BurnClaimedAt).Select(r => r.Id).Take(Batch).ToListAsync(ct);

        var done = 0;
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await LockAsync(id, skipLocked: true, ct);
            // Re-check under the lock: another finaliser may have won, and the guard is the same as the candidate query.
            if (row is not { BytesState: BytesState.Stored, BurnClaimedAt: { } claimed }) { await tx.RollbackAsync(ct); continue; }
            var exhausted = row.BurnGrantId is { } grantId
                && await db.DownloadGrants.AnyAsync(g => g.Id == grantId && g.RequestCount >= max, ct);
            if (claimed > cutoff && !exhausted) { await tx.RollbackAsync(ct); continue; }

            await bytes.QueueDeletionAsync(id, "burn", ct);          // idempotent (task dedupe key)
            await quota.ReleaseUsedAsync(id, ct);                    // idempotent per resource; joins this transaction
            await db.Resources.Where(r => r.Id == id && r.BytesState == BytesState.Stored)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.BytesState, BytesState.Deleted).SetProperty(r => r.UpdatedAt, now), ct);
            await tx.CommitAsync(ct);
            done++;
        }
        return done;
    }

    // ------------------------------------------------------------------------------------------------
    // Purge and guest expiry (T13, T18), end of legal hold (T21)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Bytes of every resource whose link reached <c>purge_after</c>: accounts at the end of the quarantine (PD-06), guests at
    /// expiry (their purge_after is their expiry). Held bytes (bytes_state 2) are not touched; a renewed link moved its copy forward.</summary>
    public async Task<int> PurgeDueAsync(CancellationToken ct)
    {
        var now = Now;
        var servable = KindsWeCanReclaim();
        var ids = await db.Resources.AsNoTracking()
            .Where(r => r.LinkId != null && (r.BytesState == BytesState.None || r.BytesState == BytesState.Stored)
                && r.LinkPurgeAfter <= now && servable.Contains(r.Kind))
            .OrderBy(r => r.LinkPurgeAfter).Select(r => r.Id).Take(Batch).ToListAsync(ct);
        var done = 0;
        foreach (var id in ids)
            if (await ReclaimAsync(id, "purge", r => r.LinkPurgeAfter <= now, ct)) done++;
        return done;
    }

    /// <summary>T21: a legal hold that ended releases the quarantined bytes for deletion.</summary>
    public async Task<int> ReleaseEndedHoldsAsync(CancellationToken ct)
    {
        var now = Now;
        var ids = await db.Resources.AsNoTracking()
            .Where(r => r.BytesState == BytesState.Quarantined && r.LegalHoldUntil != null && r.LegalHoldUntil < now)
            .Select(r => r.Id).Take(Batch).ToListAsync(ct);
        var done = 0;
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await LockAsync(id, skipLocked: true, ct);
            if (row is not { BytesState: BytesState.Quarantined, LegalHoldUntil: { } until } || until >= now) { await tx.RollbackAsync(ct); continue; }
            await bytes.QueueDeletionAsync(id, "hold-ended", ct);
            await db.Resources.Where(r => r.Id == id && r.BytesState == BytesState.Quarantined)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.BytesState, BytesState.Deleted).SetProperty(r => r.UpdatedAt, now), ct);
            await tx.CommitAsync(ct);
            done++;
        }
        return done;
    }

    /// <summary>
    /// The shared reaction of purge, guest expiry and the C10 <see cref="LinkPurged"/>: under the row lock, re-check the guard, delete
    /// the kind's own rows, queue the byte deletion, release the used quota and the reservation, set <c>bytes_state = 3</c>.
    /// A download authorised just before this finishes with its open file descriptor; none starts afterwards because the link is purgeable.
    /// </summary>
    private async Task<bool> ReclaimAsync(Guid id, string reason, Func<ResourceRow, bool> guard, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(id, skipLocked: false, ct);
        if (row is null || row.BytesState is BytesState.Deleted or BytesState.Quarantined
            || !guard(row)) { await tx.RollbackAsync(ct); return false; }

        foreach (var handler in kindHandlers.Where(h => h.Kind == row.Kind))
            await handler.OnResourceDeletedAsync(id, ct);
        await bytes.QueueDeletionAsync(id, reason, ct);
        await quota.ReleaseUsedAsync(id, ct);
        if (row.ReservationId is { } reservationId) await quota.ReleaseAsync(reservationId, ct);
        await db.Resources.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.BytesState, BytesState.Deleted).SetProperty(r => r.UpdatedAt, now), ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Resource {ResourceId}: bytes reclaimed ({Reason})", id, reason);
        return true;
    }

    /// <summary>T16: an abuse or safety takedown moves the bytes to quarantine under a legal hold. Interim (ARC-006 Q2 OPEN): the
    /// owner's quota keeps counting until the hold ends.</summary>
    private async Task QuarantineAsync(Guid id, CancellationToken ct)
    {
        var now = Now;
        var until = now.AddDays(Math.Max(0, resourceOptions.Value.EvidenceRetentionDays));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(id, skipLocked: false, ct);
        if (row is not { BytesState: BytesState.Stored }) { await tx.RollbackAsync(ct); return; }
        await db.Resources.Where(r => r.Id == id && r.BytesState == BytesState.Stored).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.BytesState, BytesState.Quarantined).SetProperty(r => r.LegalHoldUntil, until).SetProperty(r => r.UpdatedAt, now), ct);
        await tx.CommitAsync(ct);
        await bytes.QuarantineAsync(id, until, ct); // idempotent; a retry of the event repeats the move
    }

    // ------------------------------------------------------------------------------------------------
    // Orphans (T19), grants, tombstones
    // ------------------------------------------------------------------------------------------------

    /// <summary>A resource registered but never bound to a link within 24 hours (the share flow died): its reservation, bytes and row go.</summary>
    public async Task<int> SweepOrphansAsync(CancellationToken ct)
    {
        var cutoff = Now - OrphanAge;
        var servable = KindsWeCanReclaim();
        var ids = await db.Resources.AsNoTracking()
            .Where(r => r.LinkId == null && r.CreatedAt < cutoff && servable.Contains(r.Kind))
            .OrderBy(r => r.CreatedAt).Select(r => r.Id).Take(Batch).ToListAsync(ct);
        var done = 0;
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await LockAsync(id, skipLocked: true, ct);
            if (row is null || row.LinkId is not null || row.CreatedAt >= cutoff) { await tx.RollbackAsync(ct); continue; }

            foreach (var handler in kindHandlers.Where(h => h.Kind == row.Kind))
                await handler.OnResourceDeletedAsync(id, ct);
            await bytes.QueueDeletionAsync(id, "orphan", ct);
            await quota.ReleaseUsedAsync(id, ct);
            if (row.ReservationId is { } reservationId) await quota.ReleaseAsync(reservationId, ct);
            await db.DownloadGrants.Where(g => g.ResourceId == id).ExecuteDeleteAsync(ct);
            await db.Resources.Where(r => r.Id == id && r.LinkId == null).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            done++;
        }
        return done;
    }

    /// <summary>Expired grants older than a day. Burn claiming grants stay with their resource (the counter lives in
    /// <c>resources.download_count</c>, so deleting a grant never loses a download).</summary>
    public async Task<int> CleanupGrantsAsync(CancellationToken ct)
    {
        var cutoff = Now.AddDays(-1);
        return await db.DownloadGrants
            .Where(g => !g.IsBurnClaim && g.FirstUseBy < cutoff && (g.FirstUsedAt == null || g.FirstUsedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>ARC-006 T18: the row stays as a tombstone for 30 days after its bytes were deleted and its link was purged.</summary>
    public async Task<int> DeleteTombstonesAsync(CancellationToken ct)
    {
        var cutoff = Now - TombstoneAge;
        await db.DownloadGrants
            .Where(g => db.Resources.Any(r => r.Id == g.ResourceId && r.BytesState == BytesState.Deleted && r.LinkPurgeAfter < cutoff))
            .ExecuteDeleteAsync(ct);
        return await db.Resources.Where(r => r.BytesState == BytesState.Deleted && r.LinkPurgeAfter < cutoff).ExecuteDeleteAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>The worker hosts no Snippets module: it must not mark a snippet's bytes deleted without deleting its rows.</summary>
    private ResourceKind[] KindsWeCanReclaim() =>
        [ResourceKind.File, ResourceKind.Media, .. kindHandlers.Select(h => h.Kind).Where(k => k is not (ResourceKind.File or ResourceKind.Media)).Distinct()];

    /// <summary><c>SELECT ... FOR UPDATE</c> of one row inside the caller's transaction; with <paramref name="skipLocked"/> a row another
    /// worker holds is simply not returned.</summary>
    private async Task<ResourceRow?> LockAsync(Guid id, bool skipLocked, CancellationToken ct)
    {
        var rows = skipLocked
            ? await db.Resources.FromSqlInterpolated($"SELECT * FROM resources.resources WHERE id = {id} FOR UPDATE SKIP LOCKED").AsNoTracking().ToListAsync(ct)
            : await db.Resources.FromSqlInterpolated($"SELECT * FROM resources.resources WHERE id = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct);
        return rows.FirstOrDefault();
    }
}

/// <summary>
/// Worker only (ARC-012 section 4): runs the lifecycle sweeps. Every minute: burn finaliser, purge and guest expiry, ended holds.
/// Every ten minutes: orphans, grant cleanup, tombstones. Daily at 03:00 UTC: the quota reconciliation. Each step is isolated: one
/// failure is logged and the others still run.
/// </summary>
public sealed class ResourceMaintenanceJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<ResourceMaintenanceJob> logger) : BackgroundService
{
    private DateOnly _reconciledOn;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        var tick = 0;
        do
        {
            try
            {
                await RunOnceAsync(tick % 10 == 0, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The resource maintenance tick failed; retrying at the next tick.");
            }
            tick++;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tick; public so tests and an operator command can run it.</summary>
    public async Task RunOnceAsync(bool slowSweeps, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ResourceLifecycle>();
        await Step("burn finaliser", () => lifecycle.FinaliseBurnsAsync(ct));
        await Step("purge", () => lifecycle.PurgeDueAsync(ct));
        await Step("ended holds", () => lifecycle.ReleaseEndedHoldsAsync(ct));
        if (slowSweeps)
        {
            await Step("orphans", () => lifecycle.SweepOrphansAsync(ct));
            await Step("grant cleanup", () => lifecycle.CleanupGrantsAsync(ct));
            await Step("tombstones", () => lifecycle.DeleteTombstonesAsync(ct));
        }
        var now = time.GetUtcNow();
        if (now.Hour == 3 && DateOnly.FromDateTime(now.UtcDateTime) != _reconciledOn)
        {
            _reconciledOn = DateOnly.FromDateTime(now.UtcDateTime);
            var quota = scope.ServiceProvider.GetRequiredService<ContentQuota>();
            await Step("quota reconciliation", async () => (await quota.ReconcileAsync(ct)).Drifted);
        }

        async Task Step(string name, Func<Task<int>> step)
        {
            try
            {
                var n = await step();
                if (n > 0) logger.LogInformation("Resource maintenance, {Step}: {Count}", name, n);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Resource maintenance step {Step} failed", name);
            }
        }
    }
}
