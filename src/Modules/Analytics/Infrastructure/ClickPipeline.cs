using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Domain;

namespace Zaagl.Modules.Analytics.Infrastructure;

/// <summary>
/// Analytics are best effort (spec §7.5): these counters are the honest record of what was lost, so
/// nothing downstream describes a click count as exact.
/// </summary>
public static class AnalyticsMetrics
{
    private static readonly Meter Meter = new("Zaagl.Analytics");
    public static readonly Counter<long> ClicksDroppedTotal = Meter.CreateCounter<long>("clicks_dropped_total");
    public static readonly Counter<long> ClicksLostTotal = Meter.CreateCounter<long>("clicks_lost_total");
}

/// <summary>
/// In-memory buffer between the redirect endpoint and the database writer. Spec §7.5: bounded at
/// 100,000, <see cref="BoundedChannelFullMode.DropWrite"/> so a full buffer drops the newly-arriving
/// click rather than blocking or evicting an older one, counted via the channel's own
/// <c>itemDropped</c> callback (not a TryWrite return value, which DropWrite always reports as success).
/// </summary>
public sealed class ClickChannel
{
    private readonly Channel<ClickEvent> _channel = Channel.CreateBounded<ClickEvent>(
        new BoundedChannelOptions(100_000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        },
        itemDropped: dropped => AnalyticsMetrics.ClicksDroppedTotal.Add(1, new KeyValuePair<string, object?>("link_id", dropped.LinkId)));

    public ChannelReader<ClickEvent> Reader => _channel.Reader;
    public ChannelWriter<ClickEvent> Writer => _channel.Writer;
}

public sealed class ClickRecorder(ClickChannel channel, ILogger<ClickRecorder> logger) : IClickRecorder
{
    public bool TryRecord(ClickEvent click)
    {
        // TryWrite always "succeeds" under DropWrite (the drop, if any, is reported by itemDropped
        // above); this return value only reflects whether the call itself could be made.
        var accepted = channel.Writer.TryWrite(click);
        if (!accepted) logger.LogWarning("Click channel rejected a write for link {LinkId}", click.LinkId);
        return accepted;
    }
}

/// <summary>
/// Caps raw <c>click_events</c> rows per link per hour (spec §4.4 <c>Analytics:RawEventsPerLinkPerHour</c>,
/// default 1,000): beyond the cap, only the daily/lifetime counters move, no more raw rows are written.
/// Single-instance, in-memory and approximate by design - the limit exists to bound storage for a hot
/// link, not to be an exact quota.
/// </summary>
public sealed class RawEventBudget(IConfiguration configuration)
{
    private readonly int _perLinkPerHour = configuration.GetValue("Analytics:RawEventsPerLinkPerHour", 1000);
    private readonly Dictionary<(Guid LinkId, long Hour), int> _counts = new();
    private readonly Lock _gate = new();

    public bool TryConsume(Guid linkId, DateTime occurredAtUtc)
    {
        var hour = occurredAtUtc.Ticks / TimeSpan.TicksPerHour;
        lock (_gate)
        {
            // Cheap, unbounded-growth-safe cleanup: an hour-old-or-more bucket is never read again.
            if (_counts.Count > 10_000)
                foreach (var stale in _counts.Keys.Where(k => k.Hour < hour).ToList())
                    _counts.Remove(stale);

            var key = (linkId, hour);
            var count = _counts.GetValueOrDefault(key);
            if (count >= _perLinkPerHour) return false;
            _counts[key] = count + 1;
            return true;
        }
    }
}

/// <summary>
/// Drains the click buffer in batches into PostgreSQL, then pushes human clicks to live dashboards.
/// Buffered ingestion is acceptable for click analytics; strict counters live elsewhere (see ILinkResolver).
/// Spec §7.5: bot hits never create raw <c>click_events</c> rows (only a daily bot-click counter moves);
/// a link's raw rows are capped per hour (<see cref="RawEventBudget"/>) with the counter still moving past
/// the cap; link rows are locked before the batch's tables, in id order, and dropped if a link no longer
/// exists. Deviation from the literal spec: <c>click_count</c>/<c>last_clicked_at</c> are not denormalized
/// onto <c>links.links</c> here - the Links and Analytics modules are separate schemas in this modular
/// monolith, and this app instead computes click totals by live query over click_events (NOT is_bot),
/// which stays correct at current traffic without a cross-module write path. See phase 4 report.
/// </summary>
public sealed class ClickWriterService(
    ClickChannel channel,
    RawEventBudget rawEventBudget,
    IServiceScopeFactory scopes,
    ILogger<ClickWriterService> logger) : BackgroundService
{
    private const int MaxBatch = 500;
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClickEvent>(MaxBatch);
        try
        {
            while (await channel.Reader.WaitToReadAsync(stoppingToken))
            {
                Drain(batch);
                if (batch.Count < MaxBatch)
                {
                    await Task.Delay(Linger, stoppingToken); // let a burst accumulate into one insert
                    Drain(batch);
                }
                await WriteAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }

        // Flush whatever is left so a clean shutdown loses nothing.
        Drain(batch);
        if (batch.Count > 0) await WriteAsync(batch, CancellationToken.None);
    }

    private void Drain(List<ClickEvent> batch)
    {
        while (batch.Count < MaxBatch && channel.Reader.TryRead(out var click)) batch.Add(click);
    }

    private async Task WriteAsync(List<ClickEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();

                // Drop events for links that no longer exist (e.g. purged between the click and this
                // flush) - same database, cross-schema read, no write.
                var linkIds = batch.Select(c => c.LinkId).Distinct().ToArray();
                var liveLinkIds = (await db.Database.SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM links.links WHERE id = ANY({linkIds})").ToListAsync(ct)).ToHashSet();
                batch.RemoveAll(c => !liveLinkIds.Contains(c.LinkId));
                if (batch.Count == 0) return;

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
                // Table locks before any row lock, so queued partition DDL can never strand this flush
                // holding link rows (spec §7.5 step 1).
                await db.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE ONLY analytics.click_events, analytics.link_daily_stats IN ROW EXCLUSIVE MODE", ct);

                // Raw rows beyond the per-link-per-hour budget are skipped; the daily counter below
                // still moves for every non-bot click regardless of the raw-row budget.
                var rawRows = batch
                    .Where(c => !c.IsBot && rawEventBudget.TryConsume(c.LinkId, c.OccurredAt))
                    .Select(c => new ClickEventRow
                    {
                        LinkId = c.LinkId,
                        OwnerId = c.OwnerId,
                        OccurredAt = c.OccurredAt,
                        VisitorHash = c.VisitorHash,
                        ReferrerHost = c.ReferrerHost,
                        Country = c.Country,
                        City = c.City,
                        Os = c.Os,
                        Browser = c.Browser,
                        DeviceType = c.DeviceType,
                        IsBot = false,
                    })
                    .ToList();
                if (rawRows.Count > 0)
                {
                    db.ChangeTracker.AutoDetectChangesEnabled = false;
                    db.ClickEvents.AddRange(rawRows);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.AutoDetectChangesEnabled = true;
                    foreach (var row in rawRows) db.Entry(row).State = EntityState.Detached;
                }

                await UpsertDailyStatsAsync(db, batch, ct);

                await tx.CommitAsync(ct);
                await PublishLiveAsync(scope.ServiceProvider, batch, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Writing {Count} clicks failed (attempt {Attempt}/3)", batch.Count, attempt);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
        AnalyticsMetrics.ClicksLostTotal.Add(batch.Count);
        logger.LogError("Dropped {Count} clicks after repeated write failures", batch.Count);
    }

    /// <summary>One row per (link, UTC day) in this batch; clicks = non-bot, bot_clicks = bot.</summary>
    private static async Task UpsertDailyStatsAsync(AnalyticsDbContext db, List<ClickEvent> batch, CancellationToken ct)
    {
        foreach (var g in batch.GroupBy(c => (c.LinkId, Day: DateOnly.FromDateTime(c.OccurredAt))))
        {
            var clicks = g.Count(c => !c.IsBot);
            var botClicks = g.Count(c => c.IsBot);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO analytics.link_daily_stats (link_id, day, clicks, bot_clicks)
                VALUES ({g.Key.LinkId}, {g.Key.Day}, {clicks}, {botClicks})
                ON CONFLICT (link_id, day) DO UPDATE SET
                    clicks = analytics.link_daily_stats.clicks + {clicks},
                    bot_clicks = analytics.link_daily_stats.bot_clicks + {botClicks}
                """, ct);
        }
    }

    private async Task PublishLiveAsync(IServiceProvider services, List<ClickEvent> batch, CancellationToken ct)
    {
        var publisher = services.GetService<ILiveClickPublisher>();
        if (publisher is null) return;

        var live = batch.Where(c => !c.IsBot)
            .Select(c => new LiveClick(c.LinkId, c.OwnerId, c.OccurredAt, c.ReferrerHost, c.Country, c.City, c.Os, c.Browser, c.DeviceType))
            .ToList();
        if (live.Count == 0) return;

        try
        {
            await publisher.PublishAsync(live, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Live click publish failed");
        }
    }
}

/// <summary>
/// Keeps upcoming monthly partitions of click_events in place and drops ones past retention (the
/// migration creates the first ones). Both functions take <c>p_today</c> from this process's
/// <see cref="TimeProvider"/>, never SQL now(), so a fake clock in tests drives them deterministically.
/// </summary>
public sealed class PartitionMaintenanceService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<PartitionMaintenanceService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
            do
            {
                await EnsureAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT analytics.ensure_click_partitions({today}, 3)", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT analytics.drop_expired_click_partitions({today})", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Click_events partition maintenance failed");
        }
    }
}
