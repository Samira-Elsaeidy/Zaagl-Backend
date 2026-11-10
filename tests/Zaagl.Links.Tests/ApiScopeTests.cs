using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Endpoints;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Links.Tests;

/// <summary>
/// BE-001 (spec §7.4, addendum §14.4 "scopes are enforced on every endpoint"). The route set is never written
/// by hand: it is read from the real app's <see cref="EndpointDataSource"/>, so a new endpoint without a scope
/// decision fails <see cref="Every_endpoint_has_a_scope_decision"/>. The behaviour checks (403
/// insufficient-scope without the scope, through with it, JWT keeps full access) run over every scoped endpoint
/// the in-memory host maps, and that host must map every scoped endpoint of the real app.
/// </summary>
public sealed class ApiScopeTests(RealApiFactory real) : IAsyncLifetime, IClassFixture<RealApiFactory>
{
    private const string Interactive = "Interactive";
    private const string Admin = "Admin";
    private const string Hub = "/hubs/";

    /// <summary>Endpoints any caller may use without credentials, each with the reason. Everything else must
    /// carry a scope, Interactive or Admin policy.</summary>
    private static readonly Dictionary<string, string> Public = new()
    {
        ["GET /{slug:redirect}"] = "short-host redirect",
        ["HEAD /{slug:redirect}"] = "short-host redirect",
        ["* /health"] = "infrastructure",
        ["* /health/live"] = "infrastructure",
        ["* /health/ready"] = "infrastructure",
        ["GET /health/issuer"] = "infrastructure",
        ["GET /account/confirm-email"] = "static fragment page",
        ["GET /account/reset-password"] = "static fragment page",
        ["POST /api/auth/register"] = "sign-up",
        ["POST /api/auth/resend-confirmation"] = "sign-up",
        ["POST /api/auth/confirm-email"] = "token in body",
        ["POST /api/auth/forgot-password"] = "password reset",
        ["POST /api/auth/reset-password"] = "token in body",
        ["POST /api/auth/login"] = "sign-in",
        ["POST /api/auth/refresh"] = "refresh token in body",
        ["POST /api/auth/logout"] = "refresh token in body",
        ["GET /api/auth/external/{provider}"] = "social sign-in",
        ["GET /api/auth/external/callback"] = "social sign-in",
        ["POST /api/auth/external/exchange"] = "one-time code in body (PD-41)",
        ["POST /api/auth/device/code"] = "RFC 8628 device authorization start (BE-024)",
        ["POST /api/auth/device/token"] = "RFC 8628 device_code in body (BE-024)",
        ["POST /api/webhooks/email"] = "HMAC signature",
        ["POST /api/billing/webhook"] = "Stripe signature",
        ["POST /api/unlock/{slug}"] = "visitor unlock",
        ["GET /api/public/stats/{slug}"] = "public stats page",
        ["POST /api/reports"] = "abuse report",
        // Guest creation: links:write is enforced by RequireScopeWhenAuthenticated and failed credentials get
        // 401 (RejectFailedCredentials); both are tested below and in CredentialRulesTests.
        ["POST /api/links/"] = "guest link creation",
        // BE-008 (Phase 1 snippets): the guest create path enforces snippets:write for credentialed callers and 401 for
        // failed ones (SnippetsApiTests); the public content API is gated by the link key and grant (ARC-004), and the
        // user-content hop by its ticket (ARC-008).
        ["POST /api/snippets/"] = "guest snippet creation",
        ["GET /api/public/content/{code}"] = "visitor read, gate: key and grant",
        ["POST /api/public/content/{code}/unlock"] = "visitor unlock",
        // BE-011/BE-012 (E1.2 S1): tus uploads accept accounts only in slice 1 (no credential: 503 guest_creation_paused on create,
        // 404 on a session route). files:write is enforced for credentialed callers by RequireScopeWhenAuthenticated and a failed
        // credential is a 401 (RejectFailedCredentials); both are tested in FilesApiTests. The download button is gate-checked.
        ["OPTIONS /api/uploads/"] = "tus capability discovery",
        ["POST /api/uploads/"] = "guest-capable create (guests off in S1)",
        ["HEAD /api/uploads/{id:guid}"] = "creator-only session route",
        ["PATCH /api/uploads/{id:guid}"] = "creator-only session route",
        ["DELETE /api/uploads/{id:guid}"] = "creator-only session route",
        ["POST /api/uploads/{id:guid}/complete"] = "creator-only session route",
        ["POST /api/uploads/simple"] = "one-request upload; scope files:write when authenticated, guest otherwise",
        ["POST /api/public/content/{code}/download"] = "visitor download button, gate: key and grant",
        ["GET /internal/delivery/{ticket}"] = "user-content host only, ticket",
        ["HEAD /internal/delivery/{ticket}"] = "user-content host only, ticket",
    };

    private WebApplication _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await ApiTestHost.StartAsync(
            services =>
            {
                services.AddAuthorizationBuilder().AddScopePolicies();
                foreach (var type in new[]
                         {
                             typeof(LinkService), typeof(FolderService), typeof(BulkLinkImporter), typeof(GuestKeyProvider),
                             typeof(GuestRateLimiter), typeof(ICaptchaVerifier), typeof(IAnalyticsQueries), typeof(IArchiveQueries),
                             typeof(ILinkDirectory), typeof(IPlanProvider),
                             typeof(Zaagl.Modules.Snippets.Application.SnippetService), typeof(Zaagl.Modules.Snippets.Contracts.ISnippetReader),
                             typeof(Zaagl.Modules.Resources.Contracts.IDownloadGrants), typeof(Zaagl.Modules.Resources.Contracts.IResourceRegistry),
                             typeof(Zaagl.Modules.Resources.Application.OwnerResources), typeof(Zaagl.Modules.Files.Contracts.IStoredObjectSummaries),
                             typeof(Zaagl.Modules.Resources.Contracts.IContentQuota), typeof(Zaagl.Modules.Billing.Contracts.IContentPlanProvider),
                             typeof(Zaagl.Modules.Files.Contracts.IUploadStats),
                         })
                    services.AddPlaceholder(type);
            },
            app =>
            {
                app.MapLinks();
                app.MapAnalytics();
                app.MapArchive();
                app.MapSnippets();
                app.MapResources();
            });
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ---- Endpoint enumeration -------------------------------------------------------------------------

    private sealed record EndpointInfo(string Key, string Method, string Pattern, bool Anonymous, IReadOnlyList<string> Policies)
    {
        public string? Scope => Policies.FirstOrDefault(p => p.StartsWith("scope:", StringComparison.Ordinal))?["scope:".Length..];
        public bool HasDecision => Policies.Any(p => p is Interactive or Admin or "AnyJwt" || p.StartsWith("scope:", StringComparison.Ordinal));
    }

    private static string Normalize(string pattern) =>
        pattern.StartsWith("/{slug:regex(", StringComparison.Ordinal) ? "/{slug:redirect}" : pattern;

    private static List<EndpointInfo> Endpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(method => new EndpointInfo(
                $"{method} {Normalize(e.RoutePattern.RawText!)}",
                method,
                e.RoutePattern.RawText!,
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).OfType<string>().ToList())))
            .ToList();

    private List<EndpointInfo> RealEndpoints() => Endpoints(real.Services);

    [Fact]
    public void Every_endpoint_has_a_scope_decision()
    {
        var endpoints = RealEndpoints();
        Assert.True(endpoints.Count > 50, $"Only {endpoints.Count} endpoints found; the enumeration is broken.");

        var missing = endpoints
            .Where(e => !Public.ContainsKey(e.Key))
            .Where(e => e.Anonymous || !e.HasDecision)
            .Select(e => $"{e.Key} (policies: {string.Join(", ", e.Policies.DefaultIfEmpty("default"))}{(e.Anonymous ? ", AllowAnonymous" : "")})")
            .ToList();
        Assert.True(missing.Count == 0,
            "Endpoints an API key can reach without a scope, Interactive or Admin policy (add one, or list the route as public):\n"
            + string.Join("\n", missing));

        var stale = Public.Keys.Where(k => endpoints.All(e => e.Key != k)).ToList();
        Assert.True(stale.Count == 0, "Public allow-list entries for routes that no longer exist:\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("GET /api/links/{id:guid}/analytics/events", ApiScopes.StatsRead)]
    [InlineData("GET /api/links/{id:guid}/analytics/utm", ApiScopes.StatsRead)]
    [InlineData("GET /api/links/{id:guid}/analytics", ApiScopes.StatsRead)]
    [InlineData("* /hubs/analytics", ApiScopes.StatsRead)]
    [InlineData("* /hubs/analytics/negotiate", ApiScopes.StatsRead)]
    public void Analytics_endpoints_and_the_hub_require_stats_read(string key, string scope)
    {
        var endpoint = Assert.Single(RealEndpoints(), e => e.Key == key);
        Assert.Equal(scope, endpoint.Scope);
        Assert.False(endpoint.Anonymous);
    }

    [Theory]
    [InlineData("GET /api/auth/me")]
    [InlineData("GET /api/billing/me")]
    [InlineData("POST /api/auth/logout-everywhere")]
    [InlineData("POST /api/auth/totp/enroll")]
    [InlineData("POST /api/auth/totp/confirm")]
    [InlineData("POST /api/auth/hub-ticket")]
    [InlineData("GET /api/api-keys/")]
    [InlineData("POST /api/api-keys/")]
    public void Account_endpoints_are_jwt_session_only(string key)
    {
        var endpoint = Assert.Single(RealEndpoints(), e => e.Key == key);
        Assert.Contains(Interactive, endpoint.Policies);
        Assert.False(endpoint.Anonymous);
    }

    [Fact]
    public void Only_the_totp_endpoints_accept_the_enrol_only_token()
    {
        var accepting = real.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<EnrolOnlyTokenAllowedMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText).Order().ToList();
        Assert.Equal(["/api/auth/totp/confirm", "/api/auth/totp/enroll"], accepting);
    }

    // ---- Behaviour over every scoped endpoint ----------------------------------------------------------

    private List<EndpointInfo> ScopedHostEndpoints() =>
        Endpoints(_host.Services).Where(e => e.Scope is not null || e.Key == "POST /api/links/").ToList();

    private static string RequiredScope(EndpointInfo e) => e.Scope ?? ApiScopes.LinksWrite; // POST /api/links: the filter's scope

    [Fact]
    public void The_behaviour_host_maps_every_scoped_endpoint_of_the_app()
    {
        var hostKeys = ScopedHostEndpoints().Select(e => e.Key).ToHashSet();
        var unmapped = RealEndpoints()
            .Where(e => e.Scope is not null && !e.Pattern.StartsWith(Hub, StringComparison.Ordinal) && !hostKeys.Contains(e.Key))
            .Select(e => e.Key).ToList();
        Assert.True(unmapped.Count == 0, "Scoped endpoints without a behaviour check (map their group in InitializeAsync):\n" + string.Join("\n", unmapped));
        Assert.Contains("GET /api/links/{id:guid}/analytics/events", hostKeys);
        Assert.Contains("GET /api/links/{id:guid}/analytics/utm", hostKeys);
    }

    [Fact]
    public async Task An_api_key_without_the_required_scope_gets_403_insufficient_scope()
    {
        var failures = new List<string>();
        foreach (var e in ScopedHostEndpoints())
        {
            var scope = RequiredScope(e);
            var response = await SendAsync(e.Method, e.Pattern, "apikey:" + string.Join(' ', ApiScopes.All.Where(s => s != scope)));
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Forbidden || !body.Contains(ApiScopes.ProblemCode, StringComparison.Ordinal))
                failures.Add($"{e.Key} without {scope} -> {(int)response.StatusCode} {body}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task An_api_key_with_the_required_scope_reaches_the_handler()
    {
        var failures = new List<string>();
        foreach (var e in ScopedHostEndpoints())
        {
            var response = await SendAsync(e.Method, e.Pattern, "apikey:" + RequiredScope(e));
            if (!PassedAuthorization(response)) failures.Add($"{e.Key} with {RequiredScope(e)} -> {(int)response.StatusCode}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task A_session_jwt_keeps_full_access()
    {
        var failures = new List<string>();
        foreach (var e in ScopedHostEndpoints())
        {
            var response = await SendAsync(e.Method, e.Pattern, "jwt");
            if (!PassedAuthorization(response)) failures.Add($"{e.Key} -> {(int)response.StatusCode}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // The handler ran: either a placeholder service threw (599) or the handler answered on its own before using
    // one (the CSV importer rejects this test file with 400; slug-available without a slug answers 200; POST /api/snippets/raw wants
    // text/plain and answers 415 to the JSON body this test sends, after authorization). Never 401/403.
    private static bool PassedAuthorization(HttpResponseMessage response) =>
        (int)response.StatusCode is ApiTestHost.ReachedHandler or StatusCodes.Status400BadRequest or StatusCodes.Status415UnsupportedMediaType or (>= 200 and < 300);

    [Fact]
    public async Task A_links_read_key_cannot_create_a_link_even_though_guests_may()
    {
        var response = await SendAsync("POST", "/api/links", "apikey:" + ApiScopes.LinksRead);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ApiScopes.ProblemCode, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Anonymous_callers_are_still_challenged_on_owner_routes()
    {
        var response = await SendAsync("DELETE", $"/api/links/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Key_creation_accepts_the_phase_1_content_scopes_but_does_not_grant_them_by_default()
    {
        Assert.Equal(ApiScopes.All.Order(), IdentityService.AllowedScopes.Order());
        Assert.DoesNotContain(ApiScopes.FilesWrite, IdentityService.DefaultScopes);
        Assert.DoesNotContain(ApiScopes.SnippetsWrite, IdentityService.DefaultScopes);
        Assert.DoesNotContain(ApiScopes.ResourcesRead, IdentityService.DefaultScopes);
        Assert.Equal([ApiScopes.LinksRead, ApiScopes.LinksWrite, ApiScopes.StatsRead], IdentityService.DefaultScopes.Order());
    }

    [Fact]
    public void Auth_rate_limit_default_is_10_per_minute()
    {
        Assert.Equal(10, new RateLimitOptions().AuthPerMinute);
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string pattern, string? principal)
    {
        var path = Regex.Replace(pattern.Replace("{n:int}", "1"), @"\{[^}]+\}", _ => Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (principal is not null) request.Headers.Add(ApiTestHost.PrincipalHeader, principal);
        if (path.EndsWith("/bulk", StringComparison.Ordinal))
        {
            request.Content = new MultipartFormDataContent { { new ByteArrayContent("a,b\n"u8.ToArray()), "file", "links.csv" } };
        }
        else if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { name = "x" });
        }
        return await _client.SendAsync(request);
    }
}
