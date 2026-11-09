using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Domain;

// EF1002: the only values interpolated into SQL text are column names and the date_trunc unit, which are string
// literals or come from GranularityFor's fixed whitelist. All user-influenced values (ids, dates) are bound parameters.
#pragma warning disable EF1002

namespace Zaagl.Modules.Analytics.Infrastructure;

public sealed class AnalyticsQueries(AnalyticsDbContext db, TimeProvider time) : IAnalyticsQueries
{
    private const int MaxCustomSpanDays = 366;
    private const int TopN = 10;
    private const int TopCombinations = 20;
    private const int MaxEventPage = 100;

    // Whitelisted UTM columns; the only strings ever spliced into the UTM queries' SQL text.
    private static readonly string[] UtmColumns = ["utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content"];

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
        var (from, to, limited) = ResolveWindow(range, customFrom, customTo, retentionDays, time.GetUtcNow().UtcDateTime);
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

    public async Task<PagedClickEvents> GetEventsAsync(Guid linkId, ClickEventQuery query, CancellationToken ct)
    {
        var (from, to, limited) = ResolveWindow(
            query.Range, query.CustomFrom, query.CustomTo, query.RetentionDays, time.GetUtcNow().UtcDateTime);
        var take = Math.Clamp(query.Limit, 1, MaxEventPage);
        var order = query.OldestFirst ? "ASC" : "DESC";

        var sql = new StringBuilder("""
            SELECT id, occurred_at, referrer_host, country, city, os, browser, device_type,
                   utm_source, utm_medium, utm_campaign, utm_term, utm_content
            FROM analytics.click_events
            WHERE link_id = @link AND occurred_at >= @from AND occurred_at < @to AND NOT is_bot
            """);
        var args = new List<NpgsqlParameter>
        {
            new("link", linkId), new("from", from), new("to", to), new("take", take + 1),
        };

        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            sql.Append(" AND country = @country");
            args.Add(new("country", query.Country.Trim().ToUpperInvariant()));
        }
        if (!string.IsNullOrWhiteSpace(query.Device))
        {
            sql.Append(" AND lower(device_type) = lower(@device)");
            args.Add(new("device", query.Device.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Browser))
        {
            sql.Append(" AND lower(browser) = lower(@browser)");
            args.Add(new("browser", query.Browser.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Referrer))
        {
            if (query.Referrer.Trim().Equals("direct", StringComparison.OrdinalIgnoreCase))
                sql.Append(" AND COALESCE(referrer_host, '') = ''");
            else
            {
                sql.Append(" AND referrer_host = lower(@referrer)");
                args.Add(new("referrer", query.Referrer.Trim()));
            }
        }
        if (query.Cursor is not null)
        {
            if (!ClickEventCursor.TryDecode(query.Cursor, out var afterAt, out var afterId))
                throw new ArgumentException("Invalid cursor.", nameof(query));
            // Row comparison keeps the keyset stable when several clicks share a timestamp.
            sql.Append(query.OldestFirst
                ? " AND (occurred_at, id) > (@c_at, @c_id)"
                : " AND (occurred_at, id) < (@c_at, @c_id)");
            args.Add(new("c_at", afterAt));
            args.Add(new("c_id", afterId));
        }
        sql.Append($" ORDER BY occurred_at {order}, id {order} LIMIT @take");

        var rows = await ReadAsync(sql.ToString(), args, r => new ClickEventItem(
            r.GetGuid(0),
            DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc),
            NullableString(r, 2), NullableString(r, 3), NullableString(r, 4), NullableString(r, 5), NullableString(r, 6),
            r.GetString(7),
            NullableString(r, 8), NullableString(r, 9), NullableString(r, 10), NullableString(r, 11), NullableString(r, 12)), ct);

        string? next = null;
        if (rows.Count > take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = ClickEventCursor.Encode(rows[^1].OccurredAt, rows[^1].Id);
        }
        return new PagedClickEvents(from, to, limited, rows, next);
    }

    public async Task<UtmReport> GetUtmReportAsync(
        Guid linkId, AnalyticsRange range, DateTime? customFrom, DateTime? customTo, int? retentionDays, CancellationToken ct)
    {
        var (from, to, limited) = ResolveWindow(range, customFrom, customTo, retentionDays, time.GetUtcNow().UtcDateTime);
        List<NpgsqlParameter> Args() => [new("link", linkId), new("from", from), new("to", to)];
        const string Window = "link_id = @link AND occurred_at >= @from AND occurred_at < @to AND NOT is_bot";

        // One row per click, so a click counts once in each breakdown and once in the totals.
        var totals = (await ReadAsync($$"""
            SELECT COUNT(*)::bigint,
                   COUNT(*) FILTER (WHERE COALESCE(utm_source, utm_medium, utm_campaign, utm_term, utm_content) IS NOT NULL)::bigint
            FROM analytics.click_events WHERE {{Window}}
            """, Args(), r => (Total: r.GetInt64(0), Tagged: r.GetInt64(1)), ct)).Single();

        var breakdowns = new List<IReadOnlyList<BreakdownItem>>();
        foreach (var column in UtmColumns)
        {
            breakdowns.Add(await ReadAsync($$"""
                SELECT {{column}}, COUNT(*)::bigint
                FROM analytics.click_events
                WHERE {{Window}} AND {{column}} IS NOT NULL
                GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT {{TopN}}
                """, Args(), r => new BreakdownItem(r.GetString(0), r.GetInt64(1)), ct));
        }

        var combinations = await ReadAsync($$"""
            SELECT utm_source, utm_medium, utm_campaign, COUNT(*)::bigint
            FROM analytics.click_events
            WHERE {{Window}} AND COALESCE(utm_source, utm_medium, utm_campaign) IS NOT NULL
            GROUP BY 1, 2, 3 ORDER BY 4 DESC, 1, 2, 3 LIMIT {{TopCombinations}}
            """, Args(), r => new UtmCampaignRow(NullableString(r, 0), NullableString(r, 1), NullableString(r, 2), r.GetInt64(3)), ct);

        return new UtmReport(from, to, limited, totals.Total, totals.Tagged,
            breakdowns[0], breakdowns[1], breakdowns[2], breakdowns[3], breakdowns[4], combinations);
    }

    private static string? NullableString(DbDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    /// <summary>Runs a read-only command on this context's connection (raw Npgsql, not Database.SqlQuery - spec §2).</summary>
    private async Task<List<T>> ReadAsync<T>(
        string sql, IEnumerable<NpgsqlParameter> parameters, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            foreach (var p in parameters) command.Parameters.Add(p);
            if (db.Database.CurrentTransaction is { } tx) command.Transaction = tx.GetDbTransaction();

            var results = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(map(reader));
            return results;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
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
        Guid linkId, Guid ownerId, string code, bool isCustom, string? targetUrl,
        DateTime createdAt, DateTime expiresAt, DateTime purgedAt, CancellationToken ct, short targetKind = 0)
    {
        if (await db.LinkArchive.AnyAsync(a => a.LinkId == linkId, ct)) return; // already archived (idempotent)
        db.LinkArchive.Add(new LinkArchiveRow
        {
            LinkId = linkId,
            OwnerId = ownerId,
            Code = code,
            IsCustom = isCustom,
            TargetUrl = targetUrl,
            TargetKind = targetKind,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            PurgedAt = purgedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteDailyStatsAsync(Guid linkId, CancellationToken ct) =>
        await db.LinkDailyStats.Where(d => d.LinkId == linkId).ExecuteDeleteAsync(ct);

    public async Task DeleteAllForOwnerAsync(Guid ownerId, IReadOnlyList<Guid> liveLinkIds, CancellationToken ct)
    {
        var archivedLinkIds = await db.LinkArchive.Where(a => a.OwnerId == ownerId).Select(a => a.LinkId).ToListAsync(ct);
        await db.LinkArchive.Where(a => a.OwnerId == ownerId).ExecuteDeleteAsync(ct);

        var allLinkIds = archivedLinkIds.Concat(liveLinkIds).Distinct().ToList();
        if (allLinkIds.Count > 0)
            await db.LinkDailyStats.Where(d => allLinkIds.Contains(d.LinkId)).ExecuteDeleteAsync(ct);
    }
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
        new(a.LinkId, a.Code, a.IsCustom, a.TargetUrl, a.CreatedAt, a.ExpiresAt, a.PurgedAt, a.TargetKind);
}
