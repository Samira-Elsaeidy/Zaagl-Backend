using System.Text.Json.Serialization;

namespace Zaagl.Modules.Links.Contracts;

// ---- Requests -------------------------------------------------------------

/// <summary>
/// Create / update payload. On update, <c>Policy</c>, <c>OpenGraph</c> and <c>Rules</c> left null are kept as they are;
/// an empty <c>Rules</c> array removes all rules. <c>CustomSlug</c> is ignored on update (slugs are immutable).
/// </summary>
public sealed record LinkInput(
    string DestinationUrl,
    string? CustomSlug = null,
    string? Title = null,
    string? Description = null,
    Guid? FolderId = null,
    string[]? Tags = null,
    bool PublicStats = false,
    bool IsActive = true,
    OpenGraphInput? OpenGraph = null,
    PolicyInput? Policy = null,
    RuleInput[]? Rules = null);

public sealed record OpenGraphInput(string? Title, string? Description, string? ImageUrl);

/// <param name="Password">Sets a new password when non-empty.</param>
/// <param name="ClearPassword">Removes the password. Ignored when <paramref name="Password"/> is set.</param>
public sealed record PolicyInput(
    string? Password = null,
    bool ClearPassword = false,
    DateTime? ExpiresAt = null,
    int? MaxClicks = null,
    string? FallbackUrl = null);

/// <param name="Kind">device | country | split</param>
/// <param name="Match">ios | android | desktop for device, ISO-3166 alpha-2 for country, null for split.</param>
public sealed record RuleInput(string Kind, string? Match, string Url, int Weight = 1, int Priority = 0);

/// <param name="Status">active | password | expired (expired also covers deactivated and click-limit reached).</param>
public sealed record ListLinksQuery(
    string? Search = null,
    Guid? FolderId = null,
    string? Tag = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 25);

public sealed record FolderInput(string Name);

/// <summary>Partial update for the two switches that don't need the whole link resent. Null leaves a flag as it is.</summary>
public sealed record LinkFlagsInput(bool? IsActive = null, bool? PublicStats = null);

// ---- Responses ------------------------------------------------------------

public sealed record RuleDto(string Kind, string? Match, string Url, int Weight, int Priority);

public sealed record OpenGraphDto(string? Title, string? Description, string? ImageUrl);

public sealed record PolicyDto(bool HasPassword, DateTime? ExpiresAt, int? MaxClicks, int ClicksUsed, string? FallbackUrl);

public sealed record LinkDto(
    Guid Id,
    string Slug,
    string DestinationUrl,
    string? Title,
    string? Description,
    Guid? FolderId,
    string[] Tags,
    bool IsActive,
    bool PublicStats,
    OpenGraphDto OpenGraph,
    PolicyDto Policy,
    IReadOnlyList<RuleDto> Rules,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsGuest = false);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record FolderDto(Guid Id, string Name, int LinkCount);

public sealed record SlugAvailabilityDto(bool Available, string? Reason);

/// <summary>Spec §7.9: the admin link view - adds everything an owner's view intentionally hides.</summary>
public sealed record AdminLinkDto(
    Guid Id, string Slug, string DestinationUrl, string TargetHost, Guid? OwnerId, string? GuestCreatorKeyHex,
    bool IsCustom, string State, string? DisabledKind, string? DisabledReason, DateTime? DisabledAt,
    DateTime? DeletedAt, DateTime? PurgeAfter, DateTime? SafetyCheckedAt, DateTime CreatedAt);

// ---- Admin (spec §7.9, §7.15) ---------------------------------------------

public sealed record DisableLinkRequest(string Reason, string Kind);
public sealed record ReinstateRequest(string Reason);

public sealed record TakedownFilter(string? TargetHost, string? GuestCreatorKey, Guid? OwnerId, DateTime? CreatedFrom, DateTime? CreatedTo);
public sealed record TakedownRequest(TakedownFilter Filter, string Reason, bool BlockDestination, bool DryRun);
public sealed record TakedownResultDto(int LinksAffected);

public sealed record CreateReportRequest(string? Code, string? Url, string Category, string? Details, string? ReporterEmail);
public sealed record ReportDto(Guid Id, string? Code, string? Url, string Category, string? Details, string? ReporterEmail, string Status, string? Resolution, DateTime CreatedAt, DateTime? ResolvedAt);
public sealed record ResolveReportRequest(string Status, string? Resolution);

public sealed record BlockedDestinationDto(Guid Id, string Kind, string Pattern, string Reason, DateTime CreatedAt);
public sealed record CreateBlockedDestinationRequest(string Kind, string Pattern, string Reason);

public sealed record BlockedAliasDto(string SlugKey, string Reason, DateTime CreatedAt);
public sealed record CreateBlockedAliasRequest(string Code, string Reason);

public sealed record AliasDenylistDto(Guid Id, string Pattern, string Kind, DateTime CreatedAt);
public sealed record CreateAliasDenylistRequest(string Pattern, string Kind);

public sealed record AliasFreezeDto(Guid Id, DateTime StartsAt, DateTime Until, string Source, string? Reason, DateTime? EndedAt);
public sealed record DeclareRestoreRequest(string Reason);
public sealed record EndFreezeRequest(string Reason);

public sealed record BulkRowResult(int Row, bool Ok, string? Slug, string? Error);

public sealed record BulkResult(int Total, int Created, int Failed, IReadOnlyList<BulkRowResult> Rows);

// ---- Cross-module contracts ----------------------------------------------

/// <summary>
/// Spec §4.3's five-state model, computed fresh every time - never stored, so it can never go stale.
/// </summary>
public enum LinkState
{
    /// <summary>now &gt;= PurgeAfter. The row may still physically exist (no purge job runs yet in this
    /// build), but it is already gone as far as any caller is concerned: 404.</summary>
    Purgeable,
    /// <summary>DeletedAt is set. 410, visible to the owner only (admin view, in a later phase).</summary>
    Deleted,
    /// <summary>DisabledAt is set (user-disabled, admin, abuse, or safety). 410, visible to the owner.</summary>
    Disabled,
    /// <summary>now &gt;= ExpiresAt, nothing else set. 410, renewable.</summary>
    Quarantined,
    /// <summary>Otherwise: redirects normally (subject to the access-policy gates: password, click limit, routing).</summary>
    Active,
}

public static class LinkStateCalculator
{
    /// <summary>
    /// Total function, spec §4.3's table literally, evaluated top to bottom, first match wins:
    /// purgeable, then deleted, then disabled, then quarantined, then active. Click limits are NOT part
    /// of this function - like the spec, they're a separate access-policy gate layered on top of an
    /// Active link (see AccessGate), not a stored state.
    /// </summary>
    public static LinkState Compute(DateTime? purgeAfter, DateTime? deletedAt, DateTime? disabledAt, DateTime? expiresAt, DateTime now)
    {
        if (purgeAfter is { } p && now >= p) return LinkState.Purgeable;
        if (deletedAt is not null) return LinkState.Deleted;
        if (disabledAt is not null) return LinkState.Disabled;
        if (expiresAt is { } e && now >= e) return LinkState.Quarantined;
        return LinkState.Active;
    }

    public static LinkState Compute(LinkSnapshot link, DateTime now) =>
        Compute(link.PurgeAfter, link.DeletedAt, link.DisabledAt, link.ExpiresAt, now);
}

/// <summary>Everything the redirect path needs about a link; cached as one unit.</summary>
public sealed record LinkSnapshot(
    Guid Id,
    Guid? OwnerId,
    string Slug,
    string DestinationUrl,
    bool IsActive,
    string? PasswordHash,
    DateTime? ExpiresAt,
    int? MaxClicks,
    string? FallbackUrl,
    string? OgTitle,
    string? OgDescription,
    string? OgImageUrl,
    bool PublicStats,
    IReadOnlyList<RuleDto> Rules,
    DateTime? PurgeAfter = null,
    DateTime? DeletedAt = null,
    DateTime? DisabledAt = null,
    string? DisabledKind = null)
{
    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    [JsonIgnore]
    public bool HasOpenGraph =>
        !string.IsNullOrEmpty(OgTitle) || !string.IsNullOrEmpty(OgDescription) || !string.IsNullOrEmpty(OgImageUrl);
}

/// <summary>Wrapper so "no such link" can be cached too.</summary>
public sealed record SnapshotEnvelope(LinkSnapshot? Snapshot);

public interface ILinkResolver
{
    Task<LinkSnapshot?> GetSnapshotAsync(string slugKey, CancellationToken cancellationToken);

    /// <summary>Atomically consumes one click of a click-limited link. False when the limit is already reached.</summary>
    Task<bool> TryConsumeClickAsync(Guid linkId, CancellationToken cancellationToken);

    /// <summary>Spec §7.12: guest links get no statistics and no raw click_events, only this aggregate
    /// counter (visible to admins, used for abuse detection, e.g. Abuse:GuestClickAlertThreshold).</summary>
    Task IncrementGuestClickCountAsync(Guid linkId, CancellationToken cancellationToken);
}

public sealed record OwnedLink(Guid Id, string Slug);

public sealed record PublicLink(Guid Id, string Slug, string DestinationUrl, string? Title);

public interface ILinkDirectory
{
    Task<OwnedLink?> FindOwnedAsync(Guid userId, Guid linkId, CancellationToken cancellationToken);

    Task<PublicLink?> FindPublicStatsAsync(string slug, CancellationToken cancellationToken);
}
