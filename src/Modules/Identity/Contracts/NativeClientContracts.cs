using System.Text.Json.Serialization;

namespace Zaagl.Modules.Identity.Contracts;

// Phase 1 contract C11 (docs/phase1-implementation/contracts.md, ADR-0007, addendum §14,
// design/ARC-014-native-client-auth.md). Types and constants only: the backend implements the device flow (BE-024)
// and keeps enforcing scopes through the API host's policies (BE-001).

/// <summary>
/// Scope names (spec §7.4, addendum §14.4). The single source of truth for the wire strings; the API host's
/// <c>ApiScopes</c> and <c>IdentityService.AllowedScopes</c> should reference these constants (BE-024).
/// JWT and device sessions hold every non-admin scope; an API key holds only the scopes it was created with.
/// </summary>
public static class TokenScopes
{
    public const string LinksRead = "links:read";
    public const string LinksWrite = "links:write";
    public const string StatsRead = "stats:read";
    public const string SnippetsWrite = "snippets:write";
    public const string FilesWrite = "files:write";
    public const string ResourcesRead = "resources:read";

    /// <summary>Problem code for a valid credential that lacks the scope: <c>403 insufficient-scope</c>.</summary>
    public const string InsufficientScopeCode = "insufficient-scope";
}

/// <summary>Public OAuth client ids of the native apps. No client secret ships in any binary (RFC 8628 §5.6).</summary>
public static class NativeClientIds
{
    public const string Cli = "zaagl-cli";
    public const string Desktop = "zaagl-desktop";
}

/// <summary>Claim names and values carried by tokens issued through the device flow.</summary>
public static class NativeClientClaims
{
    /// <summary>One of <see cref="NativeClientIds"/>.</summary>
    public const string ClientId = "client_id";

    /// <summary>Authentication method reference. Device-flow tokens carry <see cref="AmrDevice"/>; the API host's
    /// "Interactive" policy refuses them (API keys, billing, account deletion and export) and they never carry the
    /// Admin role (addendum §14.2, MUST NOT #17).</summary>
    public const string Amr = "amr";

    public const string AmrDevice = "device";

    /// <summary>Id of the device session (refresh-token family), so one device can be listed and revoked.</summary>
    public const string SessionId = "sid";
}

/// <summary>RFC 8628 wire values.</summary>
public static class DeviceFlowWire
{
    public const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";
    /// <summary>Configuration key of the activation page URL (<c>verification_uri</c>). The server MUST read the
    /// value from configuration so staging and development point at their own app host; it never builds it from a
    /// request <c>Host</c> header. <c>verification_uri_complete</c> is this value plus <c>?code=XXXX-XXXX</c>.</summary>
    public const string VerificationUriConfigKey = "DeviceAuth:VerificationUri";

    /// <summary>Production default for <see cref="VerificationUriConfigKey"/> only; not to be used directly.</summary>
    public const string DefaultVerificationUri = "https://app.zaa.gl/activate";

    /// <summary>User code shape <c>XXXX-XXXX</c> from an alphabet without look-alikes (RFC 8628 §6.1).</summary>
    public const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";
    public const int UserCodeLength = 8;

    /// <summary>Technical defaults; the server reads its own values from configuration (<c>DeviceAuth:*</c>).</summary>
    public const int DefaultExpiresInSeconds = 600;
    public const int DefaultIntervalSeconds = 5;
    public const int SlowDownIncrementSeconds = 5;

    // RFC 8628 §3.5 error codes returned by the token endpoint (HTTP 400, body { error, error_description }).
    public const string AuthorizationPending = "authorization_pending";
    public const string SlowDown = "slow_down";
    public const string AccessDenied = "access_denied";
    public const string ExpiredToken = "expired_token";
    public const string InvalidGrant = "invalid_grant";
    public const string InvalidClient = "invalid_client";
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedGrantType = "unsupported_grant_type";
}

/// <summary><c>POST /api/auth/device/code</c> request (JSON). <paramref name="DeviceName"/> and
/// <paramref name="Os"/> are untrusted display data for the device-session list.</summary>
public sealed record DeviceCodeRequest(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("device_name")] string? DeviceName,
    [property: JsonPropertyName("os")] string? Os);

/// <summary><c>POST /api/auth/device/code</c> response, RFC 8628 §3.2 field names (snake_case on the wire).</summary>
public sealed record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary><c>POST /api/auth/device/token</c> request (JSON; form encoding also accepted per RFC 8628 §3.4).</summary>
public sealed record DeviceTokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("client_id")] string ClientId);

/// <summary>Error body of the token endpoint (RFC 6749 §5.2 shape), one of the <see cref="DeviceFlowWire"/> codes.</summary>
public sealed record DeviceTokenError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>200 body of <c>POST /api/auth/device/token</c>: the <see cref="AuthResponse"/> fields plus the device session id (the <c>sid</c> claim).</summary>
public sealed record DeviceAuthResponse(
    string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt, UserDto User, Guid SessionId);

/// <summary>What <c>app.zaa.gl/activate</c> shows before the signed-in user approves (BFF → API, session JWT).</summary>
public sealed record DeviceActivationInfo(string UserCode, string ClientId, string? DeviceName, string? Os, DateTime RequestedAt, DateTime ExpiresAt);

/// <summary><c>POST /api/auth/device/activate</c>: the signed-in user approves or denies a user code.</summary>
public sealed record DeviceActivationRequest(string UserCode, bool Approve);

/// <summary>A device session in the account's session list (web settings), revocable on its own.</summary>
public sealed record DeviceSessionDto(
    Guid SessionId,
    string ClientId,
    string? DeviceName,
    string? Os,
    DateTime CreatedAt,
    DateTime? LastUsedAt);
