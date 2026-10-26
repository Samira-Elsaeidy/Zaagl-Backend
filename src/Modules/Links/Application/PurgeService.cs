using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Modules.Links.Application;

/// <summary>
/// Spec §7.8 PurgeLink + the purge job. A link becomes eligible once its state function (see
/// LinkStateCalculator) says "purgeable" - this job is what actually deletes it, on a 15-minute cycle, in
/// batches of 200 locked with FOR UPDATE SKIP LOCKED so a future second instance would split the work
/// instead of racing it. "Purge happens on time" (spec, product decision v9.2): nothing here waits on
/// email - the notification job (if a link is about to be purged) is a separate, best-effort concern.
///
/// Deliberate scoped simplification vs. the literal spec: Links and Analytics are separate DbContexts
/// (separate Postgres schemas owned by separate modules, per this solution's module-boundary rule - see
/// ClickPipeline.cs for the same precedent on reads). The archive write and the link delete are NOT one
/// single ACID transaction spanning both schemas; they're ordered (archive/stats first, delete second) so
/// a crash between them leaves an archived row with its link not yet deleted, which the next run safely
/// re-processes (ArchiveWriter.ArchiveAsync is idempotent - ON CONFLICT equivalent via an existence check).
/// </summary>
public sealed class PurgeJob(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<PurgeJob> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        int batchCount;
        do
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            var plans = scope.ServiceProvider.GetRequiredService<IPlanProvider>();
            var archive = scope.ServiceProvider.GetRequiredService<IArchiveWriter>();
            var invalidator = scope.ServiceProvider.GetRequiredService<ILinkCacheInvalidator>();

            var now = clock.GetUtcNow().UtcDateTime;
            batchCount = await PurgeBatchAsync(db, plans, archive, invalidator, now, ct);
        }
        while (batchCount >= BatchSize); // keep draining while a full batch suggests more work is waiting
    }

    private async Task<int> PurgeBatchAsync(
        LinksDbContext db, IPlanProvider plans, IArchiveWriter archive, ILinkCacheInvalidator invalidator,
        DateTime now, CancellationToken ct)
    {
        // Spec §7.8 exactly: SELECT id ... WHERE purge_after <= @now ORDER BY purge_after LIMIT 200
        // FOR UPDATE SKIP LOCKED, in a real transaction so the row locks actually hold - EF Core's LINQ
        // has no translation for SKIP LOCKED, so the id selection is raw SQL; the entities are then loaded
        // normally inside the same transaction, which still sees them under the locks just taken.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await db.Database.SqlQuery<Guid>(
            $"""
            SELECT id AS "Value" FROM links.links
            WHERE purge_after IS NOT NULL AND purge_after <= {now}
            ORDER BY purge_after
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        if (ids.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var candidates = await db.Links.Include(l => l.Policy).Where(l => ids.Contains(l.Id)).ToListAsync(ct);

        var purgedSlugKeys = new List<string>();
        foreach (var link in candidates)
        {
            // Re-check under this read, per spec §7.8: only a row whose state is genuinely still
            // "purgeable" right now is purged - the window between the query above and this check is
            // small, but the recheck is what makes it correct rather than merely likely.
            if (link.PurgeAfter is null || link.PurgeAfter > now) continue;

            // Guest links (spec §7.12): no owner, no plan, no statistics ever kept - just delete the row.
            if (link.OwnerId is { } ownerId)
            {
                var isPremium = (await plans.GetLimitsAsync(ownerId, ct)).Tier == PlanTier.Starter;
                if (isPremium)
                    await archive.ArchiveAsync(
                        link.Id, ownerId, link.Slug, link.IsCustom, link.DestinationUrl,
                        link.CreatedAt, link.Policy?.ExpiresAt ?? link.CreatedAt, now, ct);
                else
                    await archive.DeleteDailyStatsAsync(link.Id, ct);
            }

            db.Links.Remove(link);
            purgedSlugKeys.Add(link.SlugKey);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (purgedSlugKeys.Count > 0)
        {
            await invalidator.NotifyChangedAsync(purgedSlugKeys, ct);
            logger.LogInformation("Purged {Count} links.", purgedSlugKeys.Count);
        }

        return candidates.Count;
    }
}
