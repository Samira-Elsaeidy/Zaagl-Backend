using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Domain;

// EF1002: the only values interpolated into SQL text are column names and the date_trunc unit, which are string
// literals or come from GranularityFor's fixed whitelist. All user-influenced values (ids, dates) are bound parameters.
#pragma warning disable EF1002

namespace Zaagl.Modules.Analytics.Infrastructure;

public sealed class AnalyticsQueries(AnalyticsDbContext db) : IAnalyticsQueries
{
    private const int MaxCustomSpanDays = 366;
    private const int TopN = 10;

    public sealed class TotalsRow
    {
        public long Clicks { get; set; }
        public long Visitors { get; set; }
    }

    public sealed class SeriesRow
    {
        public DateTime Bucket { get; set; }
        public long Clicks { get; set; }
        public long Visitors { get; set; }
    }

    public sealed class BreakdownRow
    {
        public string Name { get; set; } = "";
        public long Clicks { get; set; }
    }

    public sealed class LinkTotalRow
    {
        public Guid LinkId { get; set; }
        public long Clicks { get; set; }
    }

    public Task<AnalyticsOverview> GetOverviewAsync(
        Guid linkId, AnalyticsRange range, DateTime? customFrom, DateTime? customTo, int? retentionDays, CancellationToken ct) =>
        OverviewAsync("link_id", linkId, range, customFrom, customTo, retentionDays, ct);

    public Task<AnalyticsOverview> GetOwnerOverviewAsync(
        Guid ownerId, AnalyticsRange range, DateTime? customFrom, DateTime? customTo, int? retentionDays, CancellationToken ct) =>
        OverviewAsync("owner_id", ownerId, range, customFrom, customTo, retentionDays, ct);

    public async Task<IReadOnlyDictionary<Guid, long>> GetClickTotalsAsync(
        IReadOnlyCollection<Guid> linkIds, CancellationToken cancellationToken)
    {
        if (linkIds.Count == 0) return new Dictionary<Guid, long>();

        // The array is wrapped in object[] so it binds as one parameter instead of expanding into several.
        var rows = await db.Database.SqlQueryRaw<LinkTotalRow>(
            """
            SELECT link_id, COUNT(*)::bigint AS clicks
            FROM analytics.click_events
            WHERE link_id = ANY({0}) AND NOT is_bot
            GROUP BY link_id
            """, new object[] { linkIds.ToArray() }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.LinkId, r => r.Clicks);
    }

    // `filterColumn` is always a literal from this class (link_id or owner_id), never user input.
    private async Task<AnalyticsOverview> OverviewAsync(
        string filterColumn,
        Guid id,
        AnalyticsRange range,
        DateTime? customFrom,
        DateTime? customTo,
        int? retentionDays,
        CancellationToken ct)
    {
        var (from, to, limited) = ResolveWindow(range, customFrom, customTo, retentionDays, DateTime.UtcNow);
        var unit = GranularityFor(to - from);

        // Same DbContext, so the queries run one after another. Bots are excluded everywhere.
        var totals = await db.Database.SqlQueryRaw<TotalsRow>(
            $$"""
            SELECT COUNT(*)::bigint AS clicks, COUNT(DISTINCT visitor_hash)::bigint AS visitors
            FROM analytics.click_events
            WHERE {{filterColumn}} = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            """, id, from, to).SingleAsync(ct);

        // `unit` comes from a fixed whitelist (GranularityFor).
        var series = await db.Database.SqlQueryRaw<SeriesRow>(
            $$"""
            SELECT date_trunc('{{unit}}', occurred_at, 'UTC') AS bucket,
                   COUNT(*)::bigint AS clicks, COUNT(DISTINCT visitor_hash)::bigint AS visitors
            FROM analytics.click_events
            WHERE {{filterColumn}} = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            GROUP BY 1 ORDER BY 1
            """, id, from, to).ToListAsync(ct);

        return new AnalyticsOverview(
            from, to, unit,
            totals.Clicks, totals.Visitors, limited,
            series.Select(s => new SeriesPoint(DateTime.SpecifyKind(s.Bucket, DateTimeKind.Utc), s.Clicks, s.Visitors)).ToList(),
            await BreakdownAsync("referrer_host", "Direct", filterColumn, id, from, to, ct),
            await BreakdownAsync("country", "Unknown", filterColumn, id, from, to, ct),
            await BreakdownAsync("city", "Unknown", filterColumn, id, from, to, ct),
            await BreakdownAsync("os", "Unknown", filterColumn, id, from, to, ct),
            await BreakdownAsync("browser", "Unknown", filterColumn, id, from, to, ct),
            await BreakdownAsync("device_type", "Unknown", filterColumn, id, from, to, ct));
    }

    private async Task<IReadOnlyList<BreakdownItem>> BreakdownAsync(
        string column, string fallback, string filterColumn, Guid id, DateTime from, DateTime to, CancellationToken ct)
    {
        // `column` and `filterColumn` are always literals from this class.
        var rows = await db.Database.SqlQueryRaw<BreakdownRow>(
            $$"""
            SELECT COALESCE(NULLIF({{column}}, ''), {3}) AS name, COUNT(*)::bigint AS clicks
            FROM analytics.click_events
            WHERE {{filterColumn}} = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT {{TopN}}
            """, id, from, to, fallback).ToListAsync(ct);
        return rows.Select(r => new BreakdownItem(r.Name, r.Clicks)).ToList();
    }

    public static (DateTime From, DateTime To, bool LimitedByPlan) ResolveWindow(
        AnalyticsRange range, DateTime? customFrom, DateTime? customTo, int? retentionDays, DateTime now)
    {
        var to = now.AddSeconds(1); // include clicks recorded this very second
        DateTime from;

        switch (range)
        {
            case AnalyticsRange.Live: from = now.AddMinutes(-30); break;
            case AnalyticsRange.Last24Hours: from = now.AddHours(-24); break;
            case AnalyticsRange.Last30Days: from = now.AddDays(-30); break;
            case AnalyticsRange.Custom:
                to = customTo is { } t ? Utc(t) : to;
                if (to > now.AddSeconds(1)) to = now.AddSeconds(1);
                from = customFrom is { } f ? Utc(f) : to.AddDays(-7);
                if (from >= to) from = to.AddDays(-1);
                if (to - from > TimeSpan.FromDays(MaxCustomSpanDays)) from = to.AddDays(-MaxCustomSpanDays);
                break;
            default: from = now.AddDays(-7); break;
        }

        var limited = false;
        if (retentionDays is { } days)
        {
            var earliest = now.AddDays(-days);
            if (from < earliest)
            {
                from = earliest;
                limited = true;
            }
            if (to <= from) to = from.AddSeconds(1);
        }
        return (from, to, limited);
    }

    public static string GranularityFor(TimeSpan span) =>
        span <= TimeSpan.FromHours(2) ? "minute"
        : span <= TimeSpan.FromDays(3) ? "hour"
        : "day";

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

/// <summary>Spec §7.8 PurgeLink's archive step, called from the Links module's purge job inside its own
/// transaction (this writes through the same DbContext instance passed to it so both land in one commit -
/// see PurgeService for why that matters).</summary>
public sealed class ArchiveWriter(AnalyticsDbContext db) : IArchiveWriter
{
    public async Task ArchiveAsync(
        Guid linkId, Guid ownerId, string code, bool isCustom, string targetUrl,
        DateTime createdAt, DateTime expiresAt, DateTime purgedAt, CancellationToken ct)
    {
        if (await db.LinkArchive.AnyAsync(a => a.LinkId == linkId, ct)) return; // already archived (idempotent)
        db.LinkArchive.Add(new LinkArchiveRow
        {
            LinkId = linkId,
            OwnerId = ownerId,
            Code = code,
            IsCustom = isCustom,
            TargetUrl = targetUrl,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            PurgedAt = purgedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteDailyStatsAsync(Guid linkId, CancellationToken ct) =>
        await db.LinkDailyStats.Where(d => d.LinkId == linkId).ExecuteDeleteAsync(ct);
}

/// <summary>Spec §7.9 archive endpoints, backed by <c>analytics.link_archive</c> (populated by the purge
/// job - empty until a link is actually purged, but the read path is correct now).</summary>
public sealed class ArchiveQueries(AnalyticsDbContext db) : IArchiveQueries
{
    public async Task<PagedArchive> ListAsync(Guid ownerId, int limit, string? cursor, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        DateTime? before = null;
        if (!string.IsNullOrEmpty(cursor) && DateTime.TryParse(cursor, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            before = parsed;

        var query = db.LinkArchive.AsNoTracking().Where(a => a.OwnerId == ownerId);
        if (before is { } b) query = query.Where(a => a.PurgedAt < b);

        var rows = await query.OrderByDescending(a => a.PurgedAt).Take(limit + 1).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var items = rows.Select(ToDto).ToList();
        var next = hasMore ? rows[^1].PurgedAt.ToString("O") : null;
        return new PagedArchive(items, next);
    }

    public async Task<ArchivedLink?> FindAsync(Guid ownerId, Guid linkId, CancellationToken ct)
    {
        var row = await db.LinkArchive.AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerId == ownerId && a.LinkId == linkId, ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<ArchivedLinkStats> GetStatsAsync(Guid linkId, CancellationToken ct)
    {
        // Raw click_events age out with retention (spec §4.4); daily stats are kept for premium owners
        // until account deletion, so they outlive the raw events and are the source here.
        var rows = await db.LinkDailyStats.AsNoTracking()
            .Where(s => s.LinkId == linkId)
            .OrderBy(s => s.Day)
            .ToListAsync(ct);
        var series = rows.Select(s => new SeriesPoint(s.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), s.Clicks, 0)).ToList();
        return new ArchivedLinkStats(series);
    }

    private static ArchivedLink ToDto(Domain.LinkArchiveRow a) =>
        new(a.LinkId, a.Code, a.IsCustom, a.TargetUrl, a.CreatedAt, a.ExpiresAt, a.PurgedAt);
}
