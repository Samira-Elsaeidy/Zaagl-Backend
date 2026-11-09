using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Billing.Infrastructure;

namespace Zaagl.Modules.Billing.Application;

/// <summary>
/// PD-58 (spec §7.4 / §8.2, review B2): delivers plan changes to <see cref="IPlanChangeListener"/>s (Identity revokes
/// API keys) after Billing's commit, with a retry. <see cref="EnqueueAsync"/> writes a marker row in the caller's
/// Billing transaction, so a crash between commit and delivery cannot lose the change; <see cref="DeliverAsync"/>
/// runs the listeners against the user's CURRENT tier and deletes the marker only when all of them succeeded.
/// A failure keeps the marker; every later billing event and <see cref="PlanChangeRetryJob"/> retry it. Listeners
/// must be idempotent. Key login still re-checks the plan, so keys stop within 60 s even while a marker waits.
/// </summary>
public sealed class PlanChangeOutbox(
    BillingDbContext db,
    PlanCatalog plans,
    IEnumerable<IPlanChangeListener> listeners,
    TimeProvider time,
    ILogger<PlanChangeOutbox> logger)
{
    public const int BatchSize = 100;

    /// <summary>Listeners run on a tier change and on every event whose resulting plan lacks API keys (so a
    /// missed revocation heals itself even when the tier did not change again).</summary>
    public bool NeedsDelivery(PlanTier previousTier, PlanTier newTier) =>
        previousTier != newTier || !plans.For(newTier).Capabilities.ApiKeys;

    /// <summary>Upserts the user's marker. Call inside the transaction that changed the tier. On conflict the
    /// original previous tier is kept and <c>version</c> is bumped.</summary>
    public Task EnqueueAsync(Guid userId, PlanTier previousTier, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO billing.pending_plan_changes (user_id, previous_tier, queued_at, version, attempts)
            VALUES ({userId}, {(int)previousTier}, {now}, 1, 0)
            ON CONFLICT (user_id) DO UPDATE
            SET queued_at = EXCLUDED.queued_at, version = billing.pending_plan_changes.version + 1
            """, ct);
    }

    /// <summary>Runs every listener for the user's pending change, if any. Returns true when nothing is left
    /// pending for the user. Never throws for a listener failure (it is logged and recorded on the marker).</summary>
    public async Task<bool> DeliverAsync(Guid userId, CancellationToken ct)
    {
        var pending = await db.PendingPlanChanges.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (pending is null) return true;

        var tier = await db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId)
            .Select(s => (PlanTier?)s.Tier).FirstOrDefaultAsync(ct) ?? PlanTier.Free;
        var previous = plans.For(pending.PreviousTier).Capabilities;
        var current = plans.For(tier).Capabilities;

        Exception? failure = null;
        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnPlanChangedAsync(userId, previous, current, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure ??= ex;
                logger.LogError(ex, "Plan-change listener {Listener} failed for user {UserId} (attempt {Attempt}); will retry",
                    listener.GetType().Name, userId, pending.Attempts + 1);
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (failure is null)
        {
            // A newer change queued meanwhile bumped the version: leave that row for its own delivery.
            await db.PendingPlanChanges.Where(p => p.UserId == userId && p.Version == pending.Version)
                .ExecuteDeleteAsync(ct);
            return true;
        }

        var error = $"{failure.GetType().Name}: {failure.Message}";
        if (error.Length > 500) error = error[..500];
        await db.PendingPlanChanges.Where(p => p.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Attempts, p => p.Attempts + 1)
                .SetProperty(p => p.LastError, error)
                .SetProperty(p => p.LastAttemptAt, now), ct);
        return false;
    }

    /// <summary>Retries up to <see cref="BatchSize"/> pending changes, oldest first. Returns how many completed.
    /// <paramref name="notAttemptedSince"/> skips markers already tried at or after that instant (a webhook passes
    /// its start time, so the change it just tried to deliver is not retried twice in the same request).</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct, DateTime? notAttemptedSince = null)
    {
        var query = db.PendingPlanChanges.AsNoTracking();
        if (notAttemptedSince is { } since)
        {
            var cutoff = since.AddTicks(-(since.Ticks % 10)); // PostgreSQL keeps microseconds
            query = query.Where(p => p.LastAttemptAt == null || p.LastAttemptAt < cutoff);
        }
        var users = await query.OrderBy(p => p.QueuedAt)
            .Select(p => p.UserId).Take(BatchSize).ToListAsync(ct);
        var done = 0;
        foreach (var userId in users)
            if (await DeliverAsync(userId, ct)) done++;
        return done;
    }
}

/// <summary>PD-58: retries pending plan-change deliveries every 5 minutes, so a failed API-key revocation completes
/// even when no further billing event arrives. Safe on several instances (listeners are idempotent).</summary>
public sealed class PlanChangeRetryJob(IServiceScopeFactory scopes, ILogger<PlanChangeRetryJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PlanChangeOutbox>().ProcessPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Plan-change retry pass failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
