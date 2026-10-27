using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.SharedKernel.Net;

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

        // ---- Registration: always the same response shape (spec §7.4, §11 #25) -------------------
        auth.MapPost("/register", async (RegisterRequest request, IdentityService identity, IHostEnvironment env, CancellationToken ct) =>
            {
                var outcome = await identity.RegisterAsync(request, ct);
                if (outcome.Error is { } error) return ApiResults.Problem(error);
                // Dev-mode shortcut (see IdentityService's doc comment): surface the token only in
                // Development so this flow is testable without a real email system (phase 5).
                var body = new RegisterAcceptedResponse("If this is a new address, check your email to confirm your account.");
                return env.IsDevelopment() && outcome.DevOnlyConfirmToken is { } token
                    ? Results.Accepted(value: new { body.Message, devOnlyConfirmToken = token })
                    : Results.Accepted(value: body);
            })
            .RequireRateLimiting("auth");

        auth.MapPost("/resend-confirmation", async (ResendConfirmationRequest request, IdentityService identity, IHostEnvironment env, CancellationToken ct) =>
            {
                var token = await identity.ResendConfirmationAsync(request.Email, ct);
                var body = new RegisterAcceptedResponse("If that address has an unconfirmed account, a new confirmation email was sent.");
                return env.IsDevelopment() && token is not null
                    ? Results.Accepted(value: new { body.Message, devOnlyConfirmToken = token })
                    : Results.Accepted(value: body);
            })
            .RequireRateLimiting("auth");

        // GET pages that read the fragment client-side and POST it - the token itself never reaches
        // the server in this request, so it never reaches a server/proxy log (spec §7.4, §11 #30).
        // Mapped on the app directly (not the /api/auth group) since they live at /account/*.
        app.MapGet("/account/confirm-email", () => Results.Content(FragmentPostPage("/api/auth/confirm-email"), "text/html"));
        app.MapGet("/account/reset-password", () => Results.Content(FragmentPostPage("/api/auth/reset-password"), "text/html"));

        auth.MapPost("/confirm-email", async (ConfirmEmailRequest request, IdentityService identity, CancellationToken ct) =>
            ApiResults.From(await identity.ConfirmEmailAsync(request.UserId, request.Token, ct), _ => Results.NoContent()));

        // ---- Password reset ------------------------------------------------------------------------
        auth.MapPost("/forgot-password", async (ForgotPasswordRequest request, IdentityService identity, IHostEnvironment env, CancellationToken ct) =>
            {
                var token = await identity.ForgotPasswordAsync(request.Email, ct);
                var body = new RegisterAcceptedResponse("If that address has a confirmed account, a password reset email was sent.");
                return env.IsDevelopment() && token is not null
                    ? Results.Accepted(value: new { body.Message, devOnlyResetToken = token })
                    : Results.Accepted(value: body);
            })
            .RequireRateLimiting("auth");

        auth.MapPost("/reset-password", async (ResetPasswordRequest request, IdentityService identity, CancellationToken ct) =>
                ApiResults.From(await identity.ResetPasswordAsync(request.UserId, request.Token, request.NewPassword, ct), _ => Results.NoContent()))
            .RequireRateLimiting("auth");

        // ---- Login, refresh, logout ------------------------------------------------------------------
        auth.MapPost("/login", async (LoginRequest request, HttpContext http, IdentityService identity, CancellationToken ct) =>
            {
                var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
                var outcome = await identity.LoginAsync(request, partition, ct);
                if (outcome.Error is { } error) return ApiResults.Problem(error);
                if (outcome.Enrol is { } enrol) return Results.Ok(new EnrolTotpResponse(enrol.EnrolToken, enrol.ExpiresAt));
                return Results.Ok(outcome.Session);
            })
            .RequireRateLimiting("auth");

        auth.MapPost("/refresh", async (RefreshRequest request, IdentityService identity, CancellationToken ct) =>
                ApiResults.From(await identity.RefreshAsync(request.RefreshToken, ct)))
            .RequireRateLimiting("auth");

        auth.MapPost("/logout", async (LogoutRequest request, IdentityService identity, CancellationToken ct) =>
        {
            await identity.LogoutAsync(request.RefreshToken, ct);
            return Results.NoContent();
        });

        auth.MapPost("/logout-everywhere", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            {
                await identity.LogoutEverywhereAsync(user.GetUserId(), ct);
                return Results.NoContent();
            })
            .RequireAuthorization();

        // ---- Admin TOTP enrollment (spec §7.4) ---------------------------------------------------
        // "Interactive" (JWT bearer, authenticated) accepts both a normal session token and the
        // short-lived enrol-only token - both are ordinary JWTs with a valid sub/sstamp, the enrol-only
        // one just carries an extra claim IdentityService checks nowhere outside these two endpoints.
        auth.MapPost("/totp/enroll", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
                ApiResults.From(await identity.StartTotpEnrollmentAsync(user.GetUserId(), ct)))
            .RequireAuthorization("Interactive");

        auth.MapPost("/totp/confirm", async (TotpConfirmRequest request, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
                ApiResults.From(await identity.ConfirmTotpAsync(user.GetUserId(), request.Code, ct), _ => Results.NoContent()))
            .RequireAuthorization("Interactive");

        auth.MapGet("/me", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
                await identity.GetUserAsync(user.GetUserId(), ct) is { } me ? Results.Ok(me) : ApiResults.NotFound("User not found."))
            .RequireAuthorization();

        // Social sign-in: the SPA navigates to /api/auth/external/google, we challenge the provider,
        // then land on the callback, issue our own tokens and hand them to the SPA in the URL fragment.
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
                ? Results.Redirect($"{frontend}/auth/callback#token={Uri.EscapeDataString(result.Value!.AccessToken)}&refresh={Uri.EscapeDataString(result.Value!.RefreshToken)}")
                : Fail(result.Error!.Code);
        });

        // API keys are managed by signed-in users only (a key cannot mint more keys).
        var keys = app.MapGroup("/api/api-keys").WithTags("API keys").RequireAuthorization("Interactive");

        keys.MapGet("", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.ListApiKeysAsync(user.GetUserId(), ct)));

        keys.MapPost("", async (CreateApiKeyRequest request, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            ApiResults.From(await identity.CreateApiKeyAsync(user.GetUserId(), request.Name, request.Scopes, ct), v => Results.Ok(v)));

        keys.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            await identity.RevokeApiKeyAsync(user.GetUserId(), id, ct) ? Results.NoContent() : ApiResults.NotFound("API key not found."));

        // ---- Email webhook (spec §7.10): hard bounce / complaint events add an address to the
        // suppression list. Signed with HMAC-SHA256 over the raw body, header "X-Signature" (hex).
        app.MapPost("/api/webhooks/email", async (HttpRequest request, IdentityService identity,
                Microsoft.Extensions.Options.IOptions<EmailOptions> options, CancellationToken ct) =>
            {
                request.EnableBuffering();
                using var reader = new StreamReader(request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(ct);
                request.Body.Position = 0;

                var secret = options.Value.WebhookSecret;
                var signature = request.Headers["X-Signature"].ToString();
                if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature))
                    return Results.Unauthorized();

                var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(body)));
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(signature.ToLowerInvariant())))
                    return Results.Unauthorized();

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("email", out var emailEl)
                    && doc.RootElement.TryGetProperty("event", out var eventEl)
                    && eventEl.GetString() is "bounce" or "complaint")
                {
                    await identity.SuppressEmailAsync(emailEl.GetString() ?? "", eventEl.GetString()!, ct);
                }
                return Results.NoContent();
            })
            .WithTags("Webhooks")
            .AllowAnonymous();

        // ---- Account deletion and export (spec §7.14), JWT only -----------------------------------
        var me = app.MapGroup("/api/me").WithTags("Account").RequireAuthorization("Interactive");

        me.MapDelete("", async ([FromBody] DeleteAccountRequest request, ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            ApiResults.From(await identity.DeleteAccountAsync(user.GetUserId(), request.Password, ct), _ => Results.NoContent()));

        me.MapGet("/export", async (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            ApiResults.From(await identity.ExportAsync(user.GetUserId(), ct)))
            .RequireRateLimiting("auth"); // spec: 1/hour - reuses the existing per-IP/per-account limiter shape
    }

    /// <summary>Spec §7.4: a minimal page that reads the token out of the URL fragment (never sent to
    /// any server by the browser) and POSTs it to the real endpoint in a request body instead - so a
    /// link scanner that only GETs this page, or any server/proxy access log, never sees the token.</summary>
    private static string FragmentPostPage(string postTo) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Zaagl</title></head><body>
        <p id="status">Working&hellip;</p>
        <script>
          (function () {
            var params = new URLSearchParams(location.hash.replace(/^#/, ''));
            var userId = params.get('u');
            var token = params.get('t');
            var status = document.getElementById('status');
            if (!userId || !token) { status.textContent = 'This link is missing its token.'; return; }
            fetch('{{postTo}}', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ userId: userId, token: token })
            }).then(function (r) {
              status.textContent = r.ok ? 'Done. You can close this tab.' : 'This link is invalid or has expired.';
            }).catch(function () { status.textContent = 'Something went wrong. Please try again.'; });
          })();
        </script>
        </body></html>
        """;
}
