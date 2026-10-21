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
    DateTime UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record FolderDto(Guid Id, string Name, int LinkCount);

public sealed record SlugAvailabilityDto(bool Available, string? Reason);

public sealed record BulkRowResult(int Row, bool Ok, string? Slug, string? Error);

public sealed record BulkResult(int Total, int Created, int Failed, IReadOnlyList<BulkRowResult> Rows);

// ---- Cross-module contracts ----------------------------------------------

/// <summary>Everything the redirect path needs about a link; cached as one unit.</summary>
public sealed record LinkSnapshot(
    Guid Id,
    Guid OwnerId,
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
    IReadOnlyList<RuleDto> Rules)
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
}

public sealed record OwnedLink(Guid Id, string Slug);

public sealed record PublicLink(Guid Id, string Slug, string DestinationUrl, string? Title);

public interface ILinkDirectory
{
    Task<OwnedLink?> FindOwnedAsync(Guid userId, Guid linkId, CancellationToken cancellationToken);

    Task<PublicLink?> FindPublicStatsAsync(string slug, CancellationToken cancellationToken);
}
