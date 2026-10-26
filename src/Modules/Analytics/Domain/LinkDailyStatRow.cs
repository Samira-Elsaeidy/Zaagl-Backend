namespace Zaagl.Modules.Analytics.Domain;

/// <summary>One link's click counters for one UTC day. Upserted by the click writer; spec §4.4.</summary>
public sealed class LinkDailyStatRow
{
    public Guid LinkId { get; set; }
    public DateOnly Day { get; set; }
    public long Clicks { get; set; }
    public long BotClicks { get; set; }
}

/// <summary>A purged link's identity and lifetime kept for premium owners' statistics (spec §4.4, §7.8).</summary>
public sealed class LinkArchiveRow
{
    public Guid LinkId { get; set; }
    public Guid OwnerId { get; set; }
    public string Code { get; set; } = "";
    public bool IsCustom { get; set; }
    public string TargetUrl { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime PurgedAt { get; set; }
}

/// <summary>Key/value settings readable by the API, writable only by migrations (spec §4.4).</summary>
public sealed class AnalyticsSettingRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
