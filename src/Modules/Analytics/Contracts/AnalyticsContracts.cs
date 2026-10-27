namespace Zaagl.Modules.Analytics.Contracts;

/// <summary>One redirect. Bot and crawler hits are recorded too, flagged, and excluded from human counts.</summary>
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
    bool IsBot);

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
}

/// <summary>A purged link kept for a premium owner's history (spec §4.4 <c>link_archive</c>, §7.9 archive endpoints).</summary>
public sealed record ArchivedLink(
    Guid LinkId,
    string Code,
    bool IsCustom,
    string TargetUrl,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime PurgedAt);

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
        Guid linkId, Guid ownerId, string code, bool isCustom, string targetUrl,
        DateTime createdAt, DateTime expiresAt, DateTime purgedAt, CancellationToken cancellationToken);

    /// <summary>Free-plan (or deleted-account) purge: the link's daily stats are deleted, never archived.</summary>
    Task DeleteDailyStatsAsync(Guid linkId, CancellationToken cancellationToken);

    /// <summary>Spec §7.14 account deletion step 2: delete every archive row owned by this user (archive
    /// rows already carry OwnerId) and the daily stats of their still-live links (<paramref name="liveLinkIds"/>,
    /// supplied by the Links-side caller since Analytics has no cross-schema view of ownership) -
    /// regardless of plan tier.</summary>
    Task DeleteAllForOwnerAsync(Guid ownerId, IReadOnlyList<Guid> liveLinkIds, CancellationToken cancellationToken);
}
