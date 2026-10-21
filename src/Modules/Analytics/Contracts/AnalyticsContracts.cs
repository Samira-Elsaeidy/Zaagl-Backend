namespace Zaagl.Modules.Analytics.Contracts;

/// <summary>One redirect. Bot and crawler hits are recorded too, flagged, and excluded from human counts.</summary>
public sealed record ClickEvent(
    Guid LinkId,
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
}
