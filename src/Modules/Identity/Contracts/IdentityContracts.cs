namespace Zaagl.Modules.Identity.Contracts;

public sealed record UserDto(Guid Id, string Email, string DisplayName, string? Role, bool EmailConfirmed);

/// <summary>Issued after a successful login (or confirmed-admin TOTP step). Carries both tokens, per
/// spec §7.4/§7.9 - this app has no browser-side BFF (§7.13 is a separate web app's concern), so the
/// SPA holds both directly, same as it already did with the single token before this phase.</summary>
public sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt, UserDto User);

/// <summary>Spec §7.4 login response: an admin who has not enrolled TOTP yet gets this instead of a full
/// session - a short-lived JWT valid only for the two endpoints below, for 10 minutes.</summary>
public sealed record EnrolTotpResponse(string EnrolToken, DateTime ExpiresAt);

/// <summary>Spec §7.4 <c>POST /api/auth/totp/enroll</c> response: the raw secret (for manual entry) and
/// the <c>otpauth://</c> URI (to render as a QR code) - not to be confused with <see cref="EnrolTotpResponse"/>.</summary>
public sealed record TotpEnrollResponse(string AuthenticatorKey, string OtpAuthUri);

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);

/// <summary>Always the same shape regardless of whether the address already has an account (spec §7.4,
/// §11 #25) - the API never reveals account existence before a correct password is supplied.</summary>
public sealed record RegisterAcceptedResponse(string Message);

public sealed record LoginRequest(string Email, string Password, string? TotpCode);

public sealed record ConfirmEmailRequest(Guid UserId, string Token);

public sealed record ResendConfirmationRequest(string Email);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record TotpConfirmRequest(string Code);

public sealed record CreateApiKeyRequest(string? Name, string[]? Scopes);

public sealed record ApiKeyDto(Guid Id, string Name, string Prefix, string[] Scopes, DateTime CreatedAt, DateTime? LastUsedAt);

public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret);

/// <summary>What an authenticated caller actually has: a user id, the scopes it's allowed to use (full
/// set for a JWT session, the key's own subset for an API key), and whether it needs TOTP still.</summary>
public sealed record AuthenticatedPrincipal(Guid UserId, string[] Scopes);
