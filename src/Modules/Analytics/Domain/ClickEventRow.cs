namespace Zaagl.Modules.Analytics.Domain;

/// <summary>Row of the time-partitioned <c>analytics.click_events</c> table (partition key: occurred_at).</summary>
public sealed class ClickEventRow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LinkId { get; set; }

    /// <summary>Denormalized from the link so account-wide analytics need no join across modules.</summary>
    public Guid OwnerId { get; set; }

    public DateTime OccurredAt { get; set; }
    public string VisitorHash { get; set; } = "";
    public string? ReferrerHost { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Os { get; set; }
    public string? Browser { get; set; }
    public string DeviceType { get; set; } = "desktop";
    public bool IsBot { get; set; }
}
