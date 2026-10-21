using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Analytics.Contracts;

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
            SELECT link_id AS "LinkId", COUNT(*)::bigint AS "Clicks"
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
            SELECT COUNT(*)::bigint AS "Clicks", COUNT(DISTINCT visitor_hash)::bigint AS "Visitors"
            FROM analytics.click_events
            WHERE {{filterColumn}} = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            """, id, from, to).SingleAsync(ct);

        // `unit` comes from a fixed whitelist (GranularityFor).
        var series = await db.Database.SqlQueryRaw<SeriesRow>(
            $$"""
            SELECT date_trunc('{{unit}}', occurred_at, 'UTC') AS "Bucket",
                   COUNT(*)::bigint AS "Clicks", COUNT(DISTINCT visitor_hash)::bigint AS "Visitors"
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
            SELECT COALESCE(NULLIF({{column}}, ''), {3}) AS "Name", COUNT(*)::bigint AS "Clicks"
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
