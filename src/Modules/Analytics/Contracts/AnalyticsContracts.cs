using System.Globalization;
using System.Text;

namespace Zaagl.Modules.Analytics.Contracts;

/// <summary>Opaque keyset cursor for the click event list: the last row's timestamp ticks and id.</summary>
public static class ClickEventCursor
{
    public static string Encode(DateTime occurredAt, Guid id) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{occurredAt.Ticks}:{id:N}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsValid(string cursor) => TryDecode(cursor, out _, out _);

    public static bool TryDecode(string cursor, out DateTime occurredAt, out Guid id)
    {
        occurredAt = default;
        id = default;
        if (cursor.Length is 0 or > 200) return false;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(b64)).Split(':');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "N", out id))
                return false;
            occurredAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// C6 (Phase 1, addendum §12): what kind of hit an event records. Stored as <c>kind smallint not null default 0</c>
/// on <c>analytics.click_events</c> (index per partition) and <c>link_daily_stats</c>. Phase 0 rows are all
/// <see cref="Redirect"/>, so URL-link dashboards show identical numbers. Guest content records nothing. Counts used
/// for burn or billing never come from this pipeline (they come from the durable download-grant table).
/// The headline "views" metric and its de-duplication are PROPOSED (PD-10) and not defined by this enum. Until PD-10
/// is approved no content event (View, Raw, Download) is written; the owner's download count always comes from the
/// grant table, never from these events.
/// </summary>
public enum ClickEventKind : short
{
    /// <summary>The 302 from <c>zaa.gl</c> (Phase 0 behaviour, URL and content links).</summary>
    Redirect = 0,
    /// <summary>The content descriptor was served to a non-bot by <c>GET /api/public/content/{code}</c>.</summary>
    View = 1,
    /// <summary>Raw snippet text was served (first authorized GET of a raw ticket).</summary>
    Raw = 2,
    /// <summary>A download ticket's first authorized GET (never HEAD, never an owner download).</summary>
    Download = 3,
}

/// <summary>One redirect. Bot and crawler hits are recorded too, flagged, and excluded from human counts.</summary>
/// <param name="Utm">UTM tags of the destination this visitor was actually sent to (null when it has none).</param>
/// <param name="Kind">C6: <see cref="ClickEventKind.Redirect"/> for every Phase 0 caller; content events set it.</param>
public sealed record ClickEvent(
    Guid LinkId,
    Guid OwnerId,
    DateTime OccurredAt,
    string VisitorHash,
    string? ReferrerHost,
    string? Country,
    string? City,
    string? Os,
    string? Browser,
    string DeviceType,
    bool IsBot,
    UtmTags? Utm = null,
    ClickEventKind Kind = ClickEventKind.Redirect);

/// <summary>
/// The five standard UTM parameters of a destination URL. The UTM builder writes them into the link's
/// destination (or a routing variant's), and the redirect does not forward the short link's own query
/// string, so the destination the visitor was sent to is the one place they can come from. Recorded per
/// click, so A/B and routing variants with different tags are attributed correctly and nothing is counted twice.
/// </summary>
public sealed record UtmTags(string? Source, string? Medium, string? Campaign, string? Term, string? Content)
{
    public const int MaxLength = 200;

    /// <summary>Tags from a destination URL, or null when it carries none of the five parameters.</summary>
    public static UtmTags? FromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Query.Length < 2)
            return null;

        string? source = null, medium = null, campaign = null, term = null, content = null;
        foreach (var pair in uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')).ToLowerInvariant();
            var value = Clean(pair[(eq + 1)..]);
            if (value is null) continue;
            switch (key)
            {
                // First occurrence wins, matching how analytics tools read a repeated parameter.
                case "utm_source": source ??= value; break;
                case "utm_medium": medium ??= value; break;
                case "utm_campaign": campaign ??= value; break;
                case "utm_term": term ??= value; break;
                case "utm_content": content ??= value; break;
            }
        }

        return source is null && medium is null && campaign is null && term is null && content is null
            ? null
            : new UtmTags(source, medium, campaign, term, content);
    }

    private static string? Clean(string raw)
    {
        string value;
        try { value = Uri.UnescapeDataString(raw.Replace('+', ' ')).Trim(); }
        catch (UriFormatException) { return null; }
        if (value.Length == 0) return null;
        return value.Length > MaxLength ? value[..MaxLength] : value;
    }
}

public interface IClickRecorder
{
    /// <summary>Hands the click to the background writer. Never blocks the redirect; returns false if the buffer is full.</summary>
    bool TryRecord(ClickEvent click);
}

public sealed record LiveClick(
    Guid LinkId,
    Guid OwnerId,
    DateTime OccurredAt,
    string? ReferrerHost,
    string? Country,
    string? City,
    string? Os,
    string? Browser,
    string DeviceType);

/// <summary>Pushes new human clicks to connected dashboards (implemented with SignalR by the host).</summary>
public interface ILiveClickPublisher
{
    Task PublishAsync(IReadOnlyList<LiveClick> clicks, CancellationToken cancellationToken);
}

public enum AnalyticsRange
{
    Live,
    Last24Hours,
    Last7Days,
    Last30Days,
    Custom,
}

public sealed record SeriesPoint(DateTime Bucket, long Clicks, long Visitors);

public sealed record BreakdownItem(string Name, long Clicks);

public sealed record AnalyticsOverview(
    DateTime From,
    DateTime To,
    string Granularity,
    long TotalClicks,
    long UniqueVisitors,
    bool RangeLimitedByPlan,
    IReadOnlyList<SeriesPoint> Series,
    IReadOnlyList<BreakdownItem> Referrers,
    IReadOnlyList<BreakdownItem> Countries,
    IReadOnlyList<BreakdownItem> Cities,
    IReadOnlyList<BreakdownItem> Os,
    IReadOnlyList<BreakdownItem> Browsers,
    IReadOnlyList<BreakdownItem> Devices);

public interface IAnalyticsQueries
{
    /// <param name="retentionDays">Oldest data the caller's plan may see; null for unlimited.</param>
    Task<AnalyticsOverview> GetOverviewAsync(
        Guid linkId,
        AnalyticsRange range,
        DateTime? customFrom,
        DateTime? customTo,
        int? retentionDays,
        CancellationToken cancellationToken);

    /// <summary>Same overview, summed over every link the user owns.</summary>
    Task<AnalyticsOverview> GetOwnerOverviewAsync(
        Guid ownerId,
        AnalyticsRange range,
        DateTime? customFrom,
        DateTime? customTo,
        int? retentionDays,
        CancellationToken cancellationToken);

    /// <summary>All-time human click totals per link, for the link library. Links without clicks are absent.</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetClickTotalsAsync(
        IReadOnlyCollection<Guid> linkIds,
        CancellationToken cancellationToken);

    /// <summary>Individual human clicks of one link, newest first by default, keyset-paginated.</summary>
    Task<PagedClickEvents> GetEventsAsync(Guid linkId, ClickEventQuery query, CancellationToken cancellationToken);

    /// <summary>Human clicks of one link grouped by each UTM parameter, over the same window rules as the overview.</summary>
    Task<UtmReport> GetUtmReportAsync(
        Guid linkId,
        AnalyticsRange range,
        DateTime? customFrom,
        DateTime? customTo,
        int? retentionDays,
        CancellationToken cancellationToken);
}

/// <summary>Filters for the click event list. Null filters are not applied.</summary>
/// <param name="Referrer">A referrer host, or <c>direct</c> for clicks with no referrer.</param>
/// <param name="Cursor">Opaque value from the previous page's <c>NextCursor</c>.</param>
public sealed record ClickEventQuery(
    AnalyticsRange Range,
    DateTime? CustomFrom,
    DateTime? CustomTo,
    int? RetentionDays,
    string? Country,
    string? Device,
    string? Browser,
    string? Referrer,
    bool OldestFirst,
    int Limit,
    string? Cursor);

/// <summary>One click as shown in the event list. Holds no IP address and no visitor identifier.</summary>
public sealed record ClickEventItem(
    Guid Id,
    DateTime OccurredAt,
    string? ReferrerHost,
    string? Country,
    string? City,
    string? Os,
    string? Browser,
    string DeviceType,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? UtmTerm,
    string? UtmContent);

public sealed record PagedClickEvents(
    DateTime From,
    DateTime To,
    bool RangeLimitedByPlan,
    IReadOnlyList<ClickEventItem> Items,
    string? NextCursor);

/// <summary>One source / medium / campaign combination and its clicks.</summary>
public sealed record UtmCampaignRow(string? Source, string? Medium, string? Campaign, long Clicks);

/// <param name="TaggedClicks">Clicks whose destination carried at least one UTM parameter.</param>
public sealed record UtmReport(
    DateTime From,
    DateTime To,
    bool RangeLimitedByPlan,
    long TotalClicks,
    long TaggedClicks,
    IReadOnlyList<BreakdownItem> Sources,
    IReadOnlyList<BreakdownItem> Mediums,
    IReadOnlyList<BreakdownItem> Campaigns,
    IReadOnlyList<BreakdownItem> Terms,
    IReadOnlyList<BreakdownItem> Contents,
    IReadOnlyList<UtmCampaignRow> Combinations);

/// <summary>A purged link kept for a premium owner's history (spec §4.4 <c>link_archive</c>, §7.9 archive endpoints).</summary>
public sealed record ArchivedLink(
    Guid LinkId,
    string Code,
    bool IsCustom,
    string? TargetUrl,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime PurgedAt,
    short TargetKind = 0);

public sealed record PagedArchive(IReadOnlyList<ArchivedLink> Items, string? NextCursor);

public sealed record ArchivedLinkStats(IReadOnlyList<SeriesPoint> Series);

public interface IArchiveQueries
{
    Task<PagedArchive> ListAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken);

    Task<ArchivedLink?> FindAsync(Guid ownerId, Guid linkId, CancellationToken cancellationToken);

    Task<ArchivedLinkStats> GetStatsAsync(Guid linkId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec §7.8 PurgeLink: a premium owner's purged link is archived here so its code/target/dates and daily
/// stats stay queryable until the owner deletes the account. Called by the Links module's purge job (cross-
/// module through Contracts only, per this solution's module-boundary rule) - never by anything else.
/// </summary>
public interface IArchiveWriter
{
    Task ArchiveAsync(
        Guid linkId, Guid ownerId, string code, bool isCustom, string? targetUrl,
        DateTime createdAt, DateTime expiresAt, DateTime purgedAt, CancellationToken cancellationToken, short targetKind = 0);

    /// <summary>Free-plan (or deleted-account) purge: the link's daily stats are deleted, never archived.</summary>
    Task DeleteDailyStatsAsync(Guid linkId, CancellationToken cancellationToken);

    /// <summary>Spec §7.14 account deletion step 2: delete every archive row owned by this user (archive
    /// rows already carry OwnerId) and the daily stats of their still-live links (<paramref name="liveLinkIds"/>,
    /// supplied by the Links-side caller since Analytics has no cross-schema view of ownership) -
    /// regardless of plan tier.</summary>
    Task DeleteAllForOwnerAsync(Guid ownerId, IReadOnlyList<Guid> liveLinkIds, CancellationToken cancellationToken);
}
