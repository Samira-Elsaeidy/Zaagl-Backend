using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Api.Tests.Harness;

namespace Zaagl.Api.Tests;

/// <summary>
/// PD-41 (approved, NOT YET IMPLEMENTED): before Stage 2 content goes live, no sign-in token reaches page
/// script. (1) Social sign-in: the callback hands the SPA/BFF a one-time code, which the BFF exchanges
/// server-side; tokens never appear in the callback URL. (2) Live statistics: a separate short-lived hub
/// ticket, minted by a JWT-only route, single-use, about 60 s.
///
/// Written before the backend landed PD-41 from the decision wording; aligned (2026-11-09, run 7) to the routes
/// the backend then added in src/Zaagl.Api/Endpoints/IdentityEndpoints.cs (callback "?code=", POST
/// /api/auth/external/exchange {code}, POST /api/auth/hub-ticket {ticket, expiresAt}, hub "?ticket=").
/// Remaining "ASSUMPTION" notes are the parts still taken from the decision rather than a written contract.
/// </summary>
[Trait("Pending", "PD-41")]
[Trait("Area", "QA-010")]
public sealed class Pd41TokenExposureTests(ExternalLoginApiFactory api) : IClassFixture<ExternalLoginApiFactory>
{
    private const string CallbackPath = "/api/auth/external/callback?provider=google";
    // ASSUMPTION: route and body shape of the BFF exchange.
    private const string ExchangePath = "/api/auth/external/exchange";
    // ASSUMPTION: route of the hub ticket mint.
    private const string HubTicketPath = "/api/auth/hub-ticket";

    private async Task<HttpResponseMessage> SocialCallbackAsync()
    {
        using var client = api.ApiClient();
        var request = new HttpRequestMessage(HttpMethod.Get, CallbackPath);
        request.Headers.Add(FakeExternalHandler.Header, $"qa-ext-{TestIps.UniqueSuffix()}");
        return await client.SendAsync(request);
    }

    private static string? ParamFrom(Uri location, string name)
    {
        foreach (var part in new[] { location.Fragment.TrimStart('#'), location.Query.TrimStart('?') })
        foreach (var pair in part.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    [Fact]
    public async Task Social_login_callback_never_puts_tokens_in_the_url()
    {
        var response = await SocialCallbackAsync();
        Assert.True((int)response.StatusCode is 302 or 303, $"callback -> {(int)response.StatusCode}");
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.DoesNotContain("#error=", location); // the fake provider signed in successfully
        foreach (var marker in new[] { "token=", "refresh=", "access_token", "refresh_token", "eyJ" }) // "eyJ": a JWT header
            Assert.False(location.Contains(marker, StringComparison.OrdinalIgnoreCase), $"Location carries '{marker}': {location}");
    }

    [Fact]
    public async Task External_exchange_returns_tokens_once_and_the_second_use_fails()
    {
        var response = await SocialCallbackAsync();
        var location = response.Headers.Location ?? throw new Xunit.Sdk.XunitException($"callback -> {(int)response.StatusCode}, no Location");
        // ASSUMPTION: the one-time code is a "code" parameter in the callback Location (fragment or query).
        var code = ParamFrom(location, "code");
        Assert.True(code is not null, $"no one-time 'code' in the callback Location: {location.OriginalString}");

        using var client = api.ApiClient();
        // ASSUMPTION: body { "code": "..." }; response has accessToken and refreshToken (AuthResponse shape).
        var first = await client.PostAsJsonAsync(ExchangePath, new { code });
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"first exchange -> {(int)first.StatusCode} {await first.Content.ReadAsStringAsync()}");
        using (var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("accessToken").GetString()));
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("refreshToken").GetString()));
        }

        var second = await client.PostAsJsonAsync(ExchangePath, new { code });
        Assert.True((int)second.StatusCode is >= 400 and < 500, $"second exchange of the same code -> {(int)second.StatusCode}");
    }

    [Fact]
    public async Task Hub_ticket_is_jwt_only()
    {
        var user = await api.Principals.CreateUserAsync(tag: "pd41-ticket");
        var key = await api.Principals.CreateApiKeyAsync(user, "links:read", "links:write", "stats:read");

        using (var anonymous = api.ApiClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(HubTicketPath, null)).StatusCode);
        using (var withKey = api.ApiClient(apiKey: key))
            Assert.Equal(HttpStatusCode.Unauthorized, (await withKey.PostAsync(HubTicketPath, null)).StatusCode);
        using var jwt = api.ApiClient(bearer: user.AccessToken);
        var minted = await jwt.PostAsync(HubTicketPath, null);
        Assert.True(minted.StatusCode == HttpStatusCode.OK, $"hub-ticket with session JWT -> {(int)minted.StatusCode} {await minted.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Hub_ticket_expires_in_about_60_seconds_and_is_single_use()
    {
        var user = await api.Principals.CreateUserAsync(tag: "pd41-ticket-use");
        using var jwt = api.ApiClient(bearer: user.AccessToken);
        var minted = await jwt.PostAsync(HubTicketPath, null);
        Assert.True(minted.StatusCode == HttpStatusCode.OK, $"hub-ticket -> {(int)minted.StatusCode}");
        var now = api.Clock.GetUtcNow(); // the app's TimeProvider (spec: MUST use TimeProvider)

        // ASSUMPTION: JSON { "ticket": "...", "expiresAt": "<UTC>" } or { "ticket", "expiresIn": <seconds> }.
        string ticket;
        double lifetime;
        using (var doc = JsonDocument.Parse(await minted.Content.ReadAsStringAsync()))
        {
            ticket = doc.RootElement.GetProperty("ticket").GetString()!;
            lifetime = doc.RootElement.TryGetProperty("expiresIn", out var secs)
                ? secs.GetDouble()
                : (doc.RootElement.GetProperty("expiresAt").GetDateTimeOffset() - now).TotalSeconds;
        }
        Assert.InRange(lifetime, 30, 90);

        // Contract as implemented (src/Zaagl.Api/Infrastructure/HubTicketAuthentication.cs): "?ticket=" on /hubs/*;
        // negotiate only checks it, the connect request (WebSocket upgrade) consumes it: one ticket, one connection.
        var t = Uri.EscapeDataString(ticket);
        using var anonymous = api.ApiClient();
        var negotiate = await anonymous.PostAsync($"/hubs/analytics/negotiate?negotiateVersion=1&ticket={t}", null);
        Assert.True(negotiate.StatusCode == HttpStatusCode.OK, $"negotiate with ticket -> {(int)negotiate.StatusCode} {await negotiate.Content.ReadAsStringAsync()}");
        string connectionToken;
        using (var doc = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync()))
            connectionToken = doc.RootElement.GetProperty("connectionToken").GetString()!;

        var first = await TryConnectAsync($"/hubs/analytics?id={Uri.EscapeDataString(connectionToken)}&ticket={t}");
        Assert.True(first is null, $"first WebSocket connect with the ticket failed: {first}");

        // The ticket is spent: a new negotiate (and therefore any new connection) with it is refused.
        var again = await anonymous.PostAsync($"/hubs/analytics/negotiate?negotiateVersion=1&ticket={t}", null);
        Assert.True(again.StatusCode == HttpStatusCode.Unauthorized, $"negotiate with a used ticket -> {(int)again.StatusCode}");
    }

    [Fact]
    public async Task Hub_rejects_a_forged_ticket()
    {
        using var anonymous = api.ApiClient();
        var r = await anonymous.PostAsync($"/hubs/analytics/negotiate?negotiateVersion=1&ticket=qa-forged-{TestIps.UniqueSuffix()}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    /// <summary>Opens a WebSocket through TestServer on the API host; returns null on success, else the error.</summary>
    private async Task<string?> TryConnectAsync(string pathAndQuery)
    {
        var ws = api.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers.Host = ZaaglApiFactory.ApiHost;
        try
        {
            using var socket = await ws.ConnectAsync(new Uri($"ws://{ZaaglApiFactory.ApiHost}{pathAndQuery}"), CancellationToken.None);
            await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "qa", CancellationToken.None);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}

/// <summary>
/// A second in-process host whose "External" (social sign-in cookie) scheme is replaced by
/// <see cref="FakeExternalHandler"/>, so the callback's success path runs without Google or GitHub.
/// Test-only: the product's callback, IdentityService.ExternalLoginAsync and token issuing are real.
/// </summary>
public sealed class ExternalLoginApiFactory : ZaaglApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                if (o.SchemeMap.TryGetValue("External", out var scheme)) scheme.HandlerType = typeof(FakeExternalHandler);
            }));
    }
}

/// <summary>Authenticates when the request carries <see cref="Header"/> (the provider user id); sign-out is a no-op.</summary>
public sealed class FakeExternalHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
{
    public const string Header = "X-Test-External-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var id)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Email, $"qa-api-{id}@example.test"),
            new Claim(ClaimTypes.Name, "QA external"),
        ], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    public Task SignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
}
