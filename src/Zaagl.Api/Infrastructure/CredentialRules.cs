using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Zaagl.Modules.Identity.Application;

namespace Zaagl.Api.Infrastructure;

/// <summary>Endpoint metadata: the Admin enrol-only token (spec §7.4) is accepted on this endpoint.</summary>
public sealed class EnrolOnlyTokenAllowedMetadata
{
    public static readonly EnrolOnlyTokenAllowedMetadata Instance = new();
}

/// <summary>Credential rules shared by the authentication setup and endpoints (spec §7.2 step 1, §7.4).</summary>
public static class CredentialRules
{
    /// <summary>Spec §7.4: the 10-minute enrol-only token is valid only on endpoints marked with this
    /// (POST /api/auth/totp/enroll and /totp/confirm). Everywhere else JWT validation fails (AZ-01).</summary>
    public static TBuilder AllowEnrolOnlyToken<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(EnrolOnlyTokenAllowedMetadata.Instance);

    public static bool IsEnrolOnly(ClaimsPrincipal? principal) =>
        principal?.HasClaim(c => c.Type == TokenService.EnrolOnlyPurposeClaim) == true;

    /// <summary>True when an enrol-only token is used on an endpoint that does not accept it.</summary>
    public static bool EnrolOnlyTokenMisused(HttpContext http, ClaimsPrincipal? principal) =>
        IsEnrolOnly(principal) && http.GetEndpoint()?.Metadata.GetMetadata<EnrolOnlyTokenAllowedMetadata>() is null;

    /// <summary>True when the request carries credentials (a bearer token or an API key).</summary>
    public static bool HasCredentials(HttpRequest request) =>
        !string.IsNullOrWhiteSpace(request.Headers.Authorization.ToString())
        || request.Headers.ContainsKey(ApiKeyDefaults.Header);

    /// <summary>
    /// Spec §7.2 step 1 and MUST NOT #27 (PD-36): on endpoints that also serve anonymous callers (guest
    /// creation), credentials that are present but fail authentication (wrong, revoked or expired API key or
    /// JWT) answer 401 and never fall through to the guest path. No credentials at all stays a guest request.
    /// </summary>
    public static TBuilder RejectFailedCredentials<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.User.Identity?.IsAuthenticated != true && HasCredentials(http.Request))
            {
                var scheme = http.Request.Headers.ContainsKey(ApiKeyDefaults.Header)
                    ? ApiKeyDefaults.Scheme
                    : JwtBearerDefaults.AuthenticationScheme;
                return Results.Challenge(authenticationSchemes: [scheme]);
            }
            return await next(context);
        });
}
