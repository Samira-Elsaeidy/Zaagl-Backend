using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Analytics.Contracts;

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

    public async Task<AnalyticsOverview> GetOverviewAsync(
        Guid linkId,
        AnalyticsRange range,
        DateTime? customFrom,
        DateTime? customTo,
        int? retentionDays,
        CancellationToken cancellationToken)
    {
        var (from, to, limited) = ResolveWindow(range, customFrom, customTo, retentionDays, DateTime.UtcNow);
        var unit = GranularityFor(to - from);

        // Same DbContext, so the queries run one after another. Bots are excluded everywhere.
        var totals = await db.Database.SqlQueryRaw<TotalsRow>(
            """
            SELECT COUNT(*)::bigint AS "Clicks", COUNT(DISTINCT visitor_hash)::bigint AS "Visitors"
            FROM analytics.click_events
            WHERE link_id = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            """, linkId, from, to).SingleAsync(cancellationToken);

        // `unit` comes from a fixed whitelist (GranularityFor), never from user input.
        var series = await db.Database.SqlQueryRaw<SeriesRow>(
            $$"""
            SELECT date_trunc('{{unit}}', occurred_at, 'UTC') AS "Bucket",
                   COUNT(*)::bigint AS "Clicks", COUNT(DISTINCT visitor_hash)::bigint AS "Visitors"
            FROM analytics.click_events
            WHERE link_id = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            GROUP BY 1 ORDER BY 1
            """, linkId, from, to).ToListAsync(cancellationToken);

        return new AnalyticsOverview(
            from, to, unit,
            totals.Clicks, totals.Visitors, limited,
            series.Select(s => new SeriesPoint(DateTime.SpecifyKind(s.Bucket, DateTimeKind.Utc), s.Clicks, s.Visitors)).ToList(),
            await BreakdownAsync("referrer_host", "Direct", linkId, from, to, cancellationToken),
            await BreakdownAsync("country", "Unknown", linkId, from, to, cancellationToken),
            await BreakdownAsync("city", "Unknown", linkId, from, to, cancellationToken),
            await BreakdownAsync("os", "Unknown", linkId, from, to, cancellationToken),
            await BreakdownAsync("browser", "Unknown", linkId, from, to, cancellationToken),
            await BreakdownAsync("device_type", "Unknown", linkId, from, to, cancellationToken));
    }

    private async Task<IReadOnlyList<BreakdownItem>> BreakdownAsync(
        string column, string fallback, Guid linkId, DateTime from, DateTime to, CancellationToken ct)
    {
        // `column` is always a literal from this class.
        var rows = await db.Database.SqlQueryRaw<BreakdownRow>(
            $$"""
            SELECT COALESCE(NULLIF({{column}}, ''), {3}) AS "Name", COUNT(*)::bigint AS "Clicks"
            FROM analytics.click_events
            WHERE link_id = {0} AND occurred_at >= {1} AND occurred_at < {2} AND NOT is_bot
            GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT {{TopN}}
            """, linkId, from, to, fallback).ToListAsync(ct);
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
