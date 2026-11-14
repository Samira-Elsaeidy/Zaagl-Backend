namespace Zaagl.Modules.Links.Contracts;

/// <summary>
/// Phase 1 CAPTCHA / guest-creation control panel (PHASE1_STATUS "CAPTCHA admin control panel"). The three guest features
/// of spec §7.12 and addendum §11, named exactly like their Turnstile actions (BE-026), so one name identifies the feature,
/// its CAPTCHA action and its audit rows.
/// </summary>
public static class GuestFeatures
{
    public const string Link = "guest-link";
    public const string Upload = "guest-upload";
    public const string Snippet = "guest-snippet";

    public static readonly string[] All = [Link, Upload, Snippet];
}

/// <summary>One guest feature: <c>Enabled = false</c> pauses it (503 <c>guest_creation_paused</c>); <c>CaptchaRequired = false</c>
/// skips the Turnstile check for this feature only. Account routes (register, forgot-password, resend-confirmation) are
/// never switchable here: spec §7.12 requires CAPTCHA on them.</summary>
public sealed record GuestFeatureSettings(bool Enabled, bool CaptchaRequired);

/// <summary>The guest limits that can change at run time. <c>PerMinute</c> and <c>GlobalPerHour</c> are spec §7.12's in-memory
/// brakes (per IP partition, per instance) shared by every guest feature; <c>ContentPerHour</c>/<c>ContentPerDay</c> are the
/// PD-14 counters shared by snippets and uploads (addendum §11.3); <c>LinksPerDay</c> is spec §7.12's guest link quota per
/// creator key. The IPv6 /56 and /48 link caps and the expiry stay configuration only.</summary>
public sealed record GuestLimitSettings(int PerMinute, int GlobalPerHour, int ContentPerHour, int ContentPerDay, int LinksPerDay);

/// <summary>The effective guest settings. <c>Version</c> is 0 while nothing was ever saved (configuration defaults apply).</summary>
public sealed record GuestSettings(
    GuestFeatureSettings Link,
    GuestFeatureSettings Upload,
    GuestFeatureSettings Snippet,
    GuestLimitSettings Limits,
    int Version,
    DateTime? UpdatedAt,
    string? UpdatedBy)
{
    public GuestFeatureSettings For(string feature) => feature switch
    {
        GuestFeatures.Link => Link,
        GuestFeatures.Upload => Upload,
        GuestFeatures.Snippet => Snippet,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown guest feature."),
    };

    public bool AllPaused => !Link.Enabled && !Upload.Enabled && !Snippet.Enabled;
}

/// <summary><c>PUT /api/admin/guest-controls</c>: the whole document. <c>Version</c>, when sent, must match the stored one
/// (409 otherwise), so two admins cannot overwrite each other's change unseen.</summary>
public sealed record UpdateGuestSettingsRequest(
    GuestFeatureSettings? Link,
    GuestFeatureSettings? Upload,
    GuestFeatureSettings? Snippet,
    GuestLimitSettings? Limits,
    int? Version,
    string? Reason);

/// <summary><c>POST /api/admin/guest-controls/pause-all</c> and <c>/resume-all</c>.</summary>
public sealed record GuestPauseRequest(string? Reason);

/// <summary><c>GET /api/admin/guest-controls</c>.</summary>
/// <param name="CaptchaConfigured">False when the server has no real Turnstile verifier (no <c>Captcha:Secret</c>, or
/// <c>Captcha:Enabled=false</c>): then no CAPTCHA is checked anywhere, whatever the toggles say.</param>
/// <param name="GuestsEnabledByConfig">Configuration kill switch <c>Guests:Enabled</c>; false turns every guest feature off
/// regardless of the panel.</param>
/// <param name="GuestUploadsEnabledByConfig">Configuration <c>Uploads:GuestsEnabled</c>; false turns guest uploads off
/// regardless of the panel.</param>
/// <param name="AccountCaptchaAlwaysOn">Always true: register, forgot-password and resend-confirmation keep CAPTCHA on
/// (spec §7.12).</param>
public sealed record GuestControlsDto(
    GuestSettings Settings,
    GuestSettings Defaults,
    bool CaptchaConfigured,
    bool GuestsEnabledByConfig,
    bool GuestUploadsEnabledByConfig,
    bool AccountCaptchaAlwaysOn,
    GuestLimitBounds Bounds);

/// <summary>The accepted range of every limit (validated on PUT; the panel uses them for its inputs).</summary>
public sealed record GuestLimitBounds(IntRange PerMinute, IntRange GlobalPerHour, IntRange ContentPerHour, IntRange ContentPerDay, IntRange LinksPerDay);

public sealed record IntRange(int Min, int Max);

/// <summary>Created-in-window counts of one guest feature, from <c>links.links</c> (guest rows only; uploads = file and media).
/// Guest rows are deleted at expiry (7 days at most), so the 7-day figure can be slightly low.</summary>
public sealed record GuestFeatureStats(string Feature, int LastHour, int Last24Hours, int Last7Days, bool Enabled, bool CaptchaRequired,
    long CaptchaFailures, long PausedRefusals);

/// <summary><c>GET /api/admin/guest-controls/stats</c>. CAPTCHA failures and paused refusals are counted in memory by this API
/// instance since <c>CountersSince</c> (they reset on restart; nothing about the visitor is stored, spec MUST NOT #26).</summary>
public sealed record GuestStatsDto(IReadOnlyList<GuestFeatureStats> Features, bool AllPaused, DateTime CountersSince, DateTime GeneratedAt);

/// <summary>One audit row. <c>Changes</c> lists every field that changed, as <c>{field, old, new}</c>.</summary>
public sealed record GuestAuditEntryDto(long Id, DateTime At, Guid? ActorId, string? ActorEmail, string Action, IReadOnlyList<GuestSettingChange> Changes, string? Reason);

public sealed record GuestSettingChange(string Field, string? Old, string? New);

/// <summary><c>GET /api/guest-status</c> (public): what the website needs before showing a guest form.</summary>
public sealed record GuestStatusDto(GuestFeatureSettings Link, GuestFeatureSettings Upload, GuestFeatureSettings Snippet);
