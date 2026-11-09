using System.Text.Json.Serialization;

namespace Zaagl.Cli.Http;

// Hand-written wire DTOs (DESKTOP_CLI_PLAN.md §4: no runtime codegen). Sources:
// - docs/phase1-implementation/design/openapi-phase1.yaml (snippets, resources, storage, device flow)
// - src/Modules/Identity/Contracts/{IdentityContracts,NativeClientContracts}.cs (AuthResponse, device flow, C11)
// - src/Modules/Links/Contracts/LinkContracts.cs (LinkInput, LinkDto, PagedResult) and LinksEndpoints.LinkListItem
// Only the fields the CLI uses are declared; unknown fields are ignored on read.

/// <summary>RFC 7807 body with the Zaagl <c>code</c> extension (BE-002).</summary>
public sealed record ProblemDto(string? Type, string? Title, int? Status, string? Code, string? Detail);

/// <summary>RFC 6749 §5.2 error body of the device token endpoint.</summary>
public sealed record OAuthErrorDto(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

// ---- Device authorization (C11, RFC 8628; snake_case on the wire) -----------------------------------------------

public sealed record DeviceCodeRequestDto(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("device_name")] string? DeviceName,
    [property: JsonPropertyName("os")] string? Os);

public sealed record DeviceCodeResponseDto(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

public sealed record DeviceTokenRequestDto(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("client_id")] string ClientId);

/// <summary>Constants mirrored from <c>Zaagl.Modules.Identity.Contracts.DeviceFlowWire</c> / <c>NativeClientIds</c>
/// (the CLI does not reference backend assemblies; a unit test pins the values).</summary>
public static class DeviceFlowWire
{
    public const string ClientId = "zaagl-cli";
    public const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";
    public const int SlowDownIncrementSeconds = 5;
    public const string AuthorizationPending = "authorization_pending";
    public const string SlowDown = "slow_down";
    public const string AccessDenied = "access_denied";
    public const string ExpiredToken = "expired_token";
    public const string InvalidGrant = "invalid_grant";
    public const string InvalidClient = "invalid_client";
}

// ---- Identity ----------------------------------------------------------------------------------------------------

public sealed record UserDto(Guid Id, string? Email, string? DisplayName, string? Role, bool EmailConfirmed);

/// <summary>AuthResponse (IdentityContracts.cs) plus <c>sessionId</c> on device-flow responses.</summary>
public sealed record AuthResponseDto(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto? User,
    Guid? SessionId);

public sealed record RefreshRequestDto(string RefreshToken);

public sealed record LogoutRequestDto(string RefreshToken);

// ---- Links (Phase 0 API) -------------------------------------------------------------------------------------------

public sealed record LinkPolicyInputDto(DateTimeOffset? ExpiresAt);

public sealed record LinkCreateRequestDto(string DestinationUrl, string? CustomSlug, string? Title, LinkPolicyInputDto? Policy);

public sealed record LinkPolicyDto(bool HasPassword, DateTimeOffset? ExpiresAt, int? MaxClicks);

/// <summary><c>shortUrl</c> is not in today's LinkDto (gap G6 / BE-LINKS-SHORTURL); read it when the server adds it.</summary>
public sealed record LinkDto(
    Guid Id,
    string Slug,
    string? DestinationUrl,
    string? Title,
    bool IsActive,
    LinkPolicyDto? Policy,
    DateTimeOffset CreatedAt,
    string? ShortUrl);

public sealed record LinkListItemDto(LinkDto Link, long Clicks);

public sealed record LinkPageDto(List<LinkListItemDto> Items, int Total, int Page, int PageSize);

// ---- Snippets and resources (openapi-phase1.yaml) ----------------------------------------------------------------

public sealed record SnippetCreatedDto(
    Guid ResourceId,
    string Code,
    string ShortUrl,
    string? LinkKey,
    int Version,
    long SizeBytes,
    int LineCount,
    DateTimeOffset ExpiresAt,
    bool IsGuest);

public sealed record ResourceSummaryDto(
    Guid Id,
    string Kind,
    string? Title,
    string Code,
    string ShortUrl,
    string State,
    string? ProcessingStatus,
    long SizeBytes,
    DateTimeOffset ExpiresAt,
    bool BurnAfterDownload,
    bool IsPublic,
    DateTimeOffset CreatedAt);

public sealed record ResourcePageDto(List<ResourceSummaryDto> Items, string? NextCursor);

public sealed record StorageLimitsDto(long? MaxFileBytes, long? MaxSnippetBytes, int? RetentionDays);

public sealed record StorageDto(StorageLimitsDto? Limits, long UsedBytes, long ReservedBytes, long? QuotaBytes);

/// <summary>Proposed non-Interactive <c>GET /api/auth/whoami</c> (ARC-014 §3.1); assumed to return UserDto.</summary>
public sealed record WhoamiDto(Guid Id, string? Email, string? DisplayName, string? Role, bool EmailConfirmed);
