using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;

namespace Zaagl.Api.Endpoints;

public static class IdentityEndpoints
{
    public const string ExternalScheme = "External";
    private static readonly Dictionary<string, string> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = "Google",
        ["github"] = "GitHub",
    };

    public static void MapIdentity(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/register", async (RegisterRequest request, IdentityService identity, CancellationToken ct) =>
                ApiResults.From(await identity.RegisterAsync(request, ct), v => Results.Ok(v)))
            .RequireRateLimiting("auth");

        auth.MapPost("/login", async (LoginRequest request, IdentityService identity, CancellationToken ct) =>
                ApiResults.From(await identity.LoginAsync(request, ct)))
            .RequireRateLimiting("auth");

        auth.MapGet("/me", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
                await identity.GetUserAsync(user.GetUserId(), ct) is { } me ? Results.Ok(me) : ApiResults.NotFound("User not found."))
            .RequireAuthorization();

        // Social sign-in: the SPA navigates to /api/auth/external/google, we challenge the provider,
        // then land on the callback, issue our own JWT and hand it to the SPA in the URL fragment.
        auth.MapGet("/external/{provider}", async (string provider, HttpContext http, IAuthenticationSchemeProvider schemes) =>
        {
            if (!Providers.TryGetValue(provider, out var scheme) || await schemes.GetSchemeAsync(scheme) is null)
                return ApiResults.NotFound("Unknown or unconfigured sign-in provider.");

            var properties = new AuthenticationProperties
            {
                RedirectUri = $"/api/auth/external/callback?provider={Uri.EscapeDataString(provider.ToLowerInvariant())}",
            };
            return Results.Challenge(properties, [scheme]);
        }).RequireRateLimiting("auth");

        auth.MapGet("/external/callback", async (
            string provider, HttpContext http, IdentityService identity, IConfiguration config, CancellationToken ct) =>
        {
            var frontend = (config["Frontend:BaseUrl"] ?? "").TrimEnd('/');
            IResult Fail(string reason) => Results.Redirect($"{frontend}/auth/callback#error={Uri.EscapeDataString(reason)}");

            var external = await http.AuthenticateAsync(ExternalScheme);
            if (!external.Succeeded || external.Principal is null) return Fail("sign_in_failed");
            await http.SignOutAsync(ExternalScheme);

            var providerUserId = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (providerUserId is null) return Fail("sign_in_failed");

            var result = await identity.ExternalLoginAsync(
                provider,
                providerUserId,
                external.Principal.FindFirstValue(ClaimTypes.Email),
                external.Principal.FindFirstValue(ClaimTypes.Name),
                ct);

            return result.IsSuccess
                ? Results.Redirect($"{frontend}/auth/callback#token={Uri.EscapeDataString(result.Value!.Token)}")
                : Fail(result.Error!.Code);
        });

        // API keys are managed by signed-in users only (a key cannot mint more keys).
        var keys = app.MapGroup("/api/api-keys").WithTags("API keys").RequireAuthorization("Interactive");

        keys.MapGet("", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.ListApiKeysAsync(user.GetUserId(), ct)));

        keys.MapPost("", async (CreateApiKeyRequest request, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            ApiResults.From(await identity.CreateApiKeyAsync(user.GetUserId(), request.Name, ct), v => Results.Ok(v)));

        keys.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            await identity.RevokeApiKeyAsync(user.GetUserId(), id, ct) ? Results.NoContent() : ApiResults.NotFound("API key not found."));
    }
}
