using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>
/// QA-010 (start): generated route x principal authorization matrix over the CURRENT routes, with spec §7.4
/// as the oracle. Routes come from the app's EndpointDataSource; every route must have an entry in
/// <see cref="AuthzOracle.Rules"/> or <see cref="AuthorizationMatrixTests.Every_route_has_an_oracle_entry"/> fails.
///
/// The matrix is split by expectation so each family has its own trait:
///  - Matrix_core: expectations the current code is believed to meet.
///  - Defect=BE-001: API key without the route's scope must get 403.
///  - Defect=AZ-01 (candidate, new): an Admin's enrol-only token must be refused everywhere except TOTP enrolment.
///  - Decision=PD-40: admins do NOT use the owner routes; a non-owner, admin included, gets 404 there. Admins act
///    on other users' links only through /api/admin/* (covered by the Admin rows below). This replaces the old
///    SpecGap=7.4 "owner or Admin" expectation (spec §7.4 amended by the addendum).
/// R1 (spec §7.4): /api/auth/me, /api/billing/me and /api/auth/logout-everywhere are JWT-only (an API key gets 401).
/// logout-everywhere and the TOTP routes are executed in <see cref="AuthRoutesTests"/> with throwaway users.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "QA-010")]
public sealed class AuthorizationMatrixTests(ZaaglApiFactory api)
{
    // ---- Coverage gate ------------------------------------------------------------------------------

    [Fact]
    public void Every_route_has_an_oracle_entry()
    {
        var keys = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(m => AuthzOracle.Key(m, e.RoutePattern.RawText!)))
            .Distinct().ToList();
        var missing = keys.Where(k => !AuthzOracle.Rules.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Routes without an authorization-matrix entry:\n" + string.Join("\n", missing));
        var stale = AuthzOracle.Rules.Keys.Where(k => !keys.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Matrix entries for routes that no longer exist:\n" + string.Join("\n", stale));
    }

    // ---- Matrix families ----------------------------------------------------------------------------

    public static TheoryData<string, string> CoreCases() => AuthzOracle.Cases(AuthzOracle.Family.Core);
    public static TheoryData<string, string> ScopeCases() => AuthzOracle.Cases(AuthzOracle.Family.ScopeBe001);
    public static TheoryData<string, string> EnrolOnlyCases() => AuthzOracle.Cases(AuthzOracle.Family.EnrolOnlyAz01);
    public static TheoryData<string, string> AdminOwnerCases() => AuthzOracle.Cases(AuthzOracle.Family.AdminOnOwnerRoutes);

    [Theory]
    [MemberData(nameof(CoreCases))]
    public Task Matrix_core(string route, string principal) => RunAsync(route, principal);

    [Theory]
    [Trait("Defect", "BE-001")]
    [MemberData(nameof(ScopeCases))]
    public Task Matrix_api_key_scope_is_enforced(string route, string principal) => RunAsync(route, principal);

    [Theory]
    [Trait("Defect", "AZ-01")]
    [MemberData(nameof(EnrolOnlyCases))]
    public Task Matrix_enrol_only_token_is_refused(string route, string principal) => RunAsync(route, principal);

    /// <summary>PD-40: an admin is a non-owner on the owner routes (404, like any other user); admin actions
    /// on other users' links go through /api/admin/*.</summary>
    [Theory]
    [Trait("Decision", "PD-40")]
    [MemberData(nameof(AdminOwnerCases))]
    public Task Matrix_admin_gets_404_on_other_users_owner_routes(string route, string principal) => RunAsync(route, principal);

    /// <summary>The headline BE-001 case, spelled out.</summary>
    [Fact]
    [Trait("Defect", "BE-001")]
    public async Task Api_key_with_only_links_read_cannot_create_a_link()
    {
        var world = await World.GetAsync(api);
        using var client = api.ApiClient(apiKey: world.Keys["links:read"]);
        var response = await client.PostAsJsonAsync("/api/links", new LinkInput($"https://example.com/qa/be001/{TestIps.UniqueSuffix()}"));
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
            $"POST /api/links with a links:read-only API key -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    // ---- Runner ---------------------------------------------------------------------------------------

    private async Task RunAsync(string route, string principal)
    {
        var rule = AuthzOracle.Rules[route];
        var expected = AuthzOracle.Expect(rule, principal);
        var world = await World.GetAsync(api);

        using var request = await BuildRequestAsync(route, rule, world);
        using var client = ClientFor(principal, world);
        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        var detail = $"{principal} {request.Method} {request.RequestUri} -> {status} (expected {expected}). Body: {Truncate(await response.Content.ReadAsStringAsync())}";

        Assert.True(status < 500, detail);
        switch (expected)
        {
            case Outcome.Allowed:
                Assert.True(status is not (401 or 403) && (status != 404 || rule.AllowedMayBe404) && status != 421, detail);
                break;
            case Outcome.Unauthenticated:
                Assert.True(status == 401, detail);
                break;
            case Outcome.Forbidden:
                Assert.True(status == 403, detail);
                break;
            case Outcome.Denied:
                Assert.True(status is 401 or 403, detail);
                break;
            case Outcome.NotFound:
                Assert.True(status == 404, detail);
                break;
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    private HttpClient ClientFor(string principal, World world) => principal switch
    {
        P.Anonymous => api.ApiClient(),
        P.OwnerJwt => api.ApiClient(bearer: world.Owner.AccessToken),
        P.OtherJwt => api.ApiClient(bearer: world.Other.AccessToken),
        P.AdminJwt => api.ApiClient(World.AdminIp(), bearer: world.Admin.AccessToken),
        P.AdminOutsideNetwork => api.ApiClient(TestIps.RandomV4(), bearer: world.Admin.AccessToken),
        P.AdminEnrolOnly => api.ApiClient(World.AdminIp(), bearer: world.EnrolOnly.AccessToken),
        _ when principal.StartsWith("key:") => api.ApiClient(apiKey: world.Keys[principal[4..]]),
        _ => throw new ArgumentOutOfRangeException(nameof(principal), principal),
    };

    private async Task<HttpRequestMessage> BuildRequestAsync(string route, Rule rule, World world)
    {
        var (method, pattern) = AuthzOracle.Split(route);
        var path = pattern.TrimEnd('/');

        if (path.Contains("{id:guid}") && rule.Resource == Resource.OwnerLink || path.Contains("{id:guid}/analytics"))
        {
            var link = await api.Principals.CreateLinkAsync(world.Owner, new LinkInput($"https://example.com/qa/authz/{TestIps.UniqueSuffix()}",
                Policy: new PolicyInput(ExpiresAt: TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(10))));
            path = path.Replace("{id:guid}", link.Id.ToString());
        }
        else if (rule.Resource == Resource.OwnerFolder)
        {
            using var owner = api.ApiClient(bearer: world.Owner.AccessToken);
            var created = await owner.PostAsJsonAsync("/api/folders", new FolderInput($"qa-authz-{TestIps.UniqueSuffix()}"));
            var folder = await created.Content.ReadFromJsonAsync<FolderDto>(TestPrincipals.Json);
            path = path.Replace("{id:guid}", folder!.Id.ToString());
        }
        path = path
            .Replace("{id:guid}", Guid.NewGuid().ToString())
            .Replace("{linkId:guid}", Guid.NewGuid().ToString())
            .Replace("{code}", world.SharedLink.Slug)
            .Replace("{slug}", world.SharedLink.Slug)
            .Replace("{provider}", "nosuchprovider");
        if (route == "GET /api/links/slug-available") path += "?slug=qa" + TestIps.UniqueSuffix();

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (rule.Body is { } body) request.Content = new StringContent(body(), Encoding.UTF8, "application/json");
        return request;
    }
}

// ---- Oracle --------------------------------------------------------------------------------------------

public enum Outcome { Allowed, Unauthenticated, Forbidden, Denied, NotFound }

public enum Access
{
    /// <summary>Anyone, no credentials needed.</summary>
    Anyone,
    /// <summary>Authenticated: user JWT (all scopes) or API key with <see cref="Rule.Scope"/>. §7.4 rows for links/stats/archive.</summary>
    Scoped,
    /// <summary>POST /api/links: scoped (links:write) for callers with credentials, guest for anonymous (§7.12).</summary>
    ScopedOrGuest,
    /// <summary>The user themself, JWT only (§7.4 /keys*, /me*, billing; R1: /auth/me, /billing/me,
    /// logout-everywhere). An API key gets 401: the route only runs the JWT scheme.</summary>
    JwtOnly,
    /// <summary>Role Admin, JWT with TOTP, from Admin:AllowedNetworks.</summary>
    Admin,
}

public enum Resource { None, OwnerLink, OwnerFolder }

public sealed record Rule(Access Access, string? Scope = null, Resource Resource = Resource.None, bool Execute = true,
    bool AllowedMayBe404 = false, Func<string>? Body = null, string? Note = null);

public static class P
{
    public const string Anonymous = "anonymous";
    public const string OwnerJwt = "owner-jwt";
    public const string OtherJwt = "other-user-jwt";
    public const string AdminJwt = "admin-jwt-totp";
    public const string AdminOutsideNetwork = "admin-jwt-outside-network";
    public const string AdminEnrolOnly = "admin-enrol-only-token";
    public static readonly string[] KeyScopes = ["links:read", "links:write", "stats:read", "all"];
    public static IEnumerable<string> All =>
        new[] { Anonymous, OwnerJwt, OtherJwt, AdminJwt, AdminOutsideNetwork, AdminEnrolOnly }.Concat(KeyScopes.Select(s => "key:" + s));
}

public static class AuthzOracle
{
    public enum Family { Core, ScopeBe001, EnrolOnlyAz01, AdminOnOwnerRoutes }

    /// <summary>The short-host catch-all's regex text is an implementation detail (BE-031 changed it from
    /// {2,63} to {3,63}), so every "/{slug:regex(...)}" pattern maps to one key.</summary>
    public static string Key(string method, string pattern) =>
        pattern.StartsWith("/{slug:regex(", StringComparison.Ordinal) ? $"{method} /{{slug:redirect}}" : $"{method} {pattern}";

    public static (string Method, string Pattern) Split(string key)
    {
        var i = key.IndexOf(' ');
        return (key[..i], key[(i + 1)..]);
    }

    private static Func<string> J(object o) => () => System.Text.Json.JsonSerializer.Serialize(o, TestPrincipals.Json);
    private static Func<string> NewLink => J(new { destinationUrl = $"https://example.com/qa/authz/{TestIps.UniqueSuffix()}" });

    private const string NotExecuted = "not executed this round (destructive, global or needs external state)";

    public static readonly Dictionary<string, Rule> Rules = new()
    {
        // ---- Short host / public / infrastructure
        ["GET /{**path:nonfile}"] = new(Access.Anyone, Execute: false, Note: "short host branded 404 for unmatched browser paths"),
        ["HEAD /{**path:nonfile}"] = new(Access.Anyone, Execute: false, Note: "short host branded 404 for unmatched browser paths"),
        ["GET /{slug:redirect}"] = new(Access.Anyone, Execute: false, Note: "short host; covered by QA-008"),
        ["HEAD /{slug:redirect}"] = new(Access.Anyone, Execute: false, Note: "short host; covered by QA-008"),
        ["* /health"] = new(Access.Anyone, Execute: false),
        ["* /health/live"] = new(Access.Anyone, Execute: false),
        ["* /health/ready"] = new(Access.Anyone, Execute: false),
        ["GET /health/issuer"] = new(Access.Anyone, Execute: false),
        ["* /hubs/analytics"] = new(Access.Scoped, "stats:read", Execute: false, Note: "WebSocket transport; negotiate is the gate"),
        ["* /hubs/analytics/negotiate"] = new(Access.Scoped, "stats:read", Execute: false, Note: "executed in AuthRoutesTests (Hub_negotiate_*)"),
        ["GET /account/confirm-email"] = new(Access.Anyone, Execute: false),
        ["GET /account/reset-password"] = new(Access.Anyone, Execute: false),
        ["POST /api/unlock/{slug}"] = new(Access.Anyone, Body: J(new { password = "x" })),
        ["GET /api/public/stats/{slug}"] = new(Access.Anyone, AllowedMayBe404: true),
        ["POST /api/reports"] = new(Access.Anyone, Execute: false, Note: "creates a report row visible to admins; TODO"),
        ["POST /api/webhooks/email"] = new(Access.Anyone, Execute: false, Note: "signature-gated; TODO: unsigned -> 401 test"),
        ["POST /api/billing/webhook"] = new(Access.Anyone, Execute: false, Note: "Stripe signature-gated; TODO"),

        // ---- Phase 1 snippets (BE-008). Executed in SnippetsApiTests with the user-content host configured.
        ["POST /api/snippets/"] = new(Access.ScopedOrGuest, "snippets:write", Execute: false, Note: "BE-008: guest, owner, key scope and failed-credential rows in SnippetsApiTests"),
        ["GET /api/snippets/{id:guid}"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "BE-008: owner, other user (404) and scope in SnippetsApiTests"),
        ["PUT /api/snippets/{id:guid}"] = new(Access.Scoped, "snippets:write", Resource.OwnerLink, Execute: false, Note: "BE-008: owner, other user (404) and scope in SnippetsApiTests"),
        ["GET /api/snippets/{id:guid}/versions"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "BE-008: SnippetsApiTests"),
        ["GET /api/snippets/{id:guid}/versions/{n:int}"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "BE-008: SnippetsApiTests"),
        ["POST /api/snippets/raw"] = new(Access.Scoped, "snippets:write", Execute: false, Note: "BE-010: no guest path; scope, 401 and 415 rows in SnippetsPolishTests"),
        ["DELETE /api/snippets/{id:guid}"] = new(Access.Scoped, "snippets:write", Resource.OwnerLink, Execute: false, Note: "BE-008 delete: owner, other user (404), scope in SnippetsPolishTests"),
        ["GET /api/snippets/{id:guid}/diff"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "BE-009: owner, other user (404), 401 in SnippetsPolishTests"),
        ["GET /api/public/content/{code}"] = new(Access.Anyone, AllowedMayBe404: true, Execute: false, Note: "BE-008: gate rows in SnippetsApiTests"),
        ["POST /api/public/content/{code}/unlock"] = new(Access.Anyone, Execute: false, Note: "BE-008: SnippetsApiTests"),
        ["GET /internal/delivery/{ticket}"] = new(Access.Anyone, Execute: false, Note: "BE-008: user-content host only; ticket-gated; SnippetsApiTests"),

        // ---- Phase 1 E1.2 S1 (BE-011, BE-012, BE-017): executed in FilesApiTests (owner, other user 404, scope, failed credential).
        ["OPTIONS /api/uploads/"] = new(Access.Anyone, Execute: false, Note: "tus discovery; FilesApiTests"),
        ["POST /api/uploads/"] = new(Access.ScopedOrGuest, "files:write", Execute: false, Note: "S1: accounts only (no credential 503); FilesApiTests"),
        ["HEAD /api/uploads/{id:guid}"] = new(Access.ScopedOrGuest, "files:write", Execute: false, Note: "creator-only session; FilesApiTests"),
        ["PATCH /api/uploads/{id:guid}"] = new(Access.ScopedOrGuest, "files:write", Execute: false, Note: "creator-only session; FilesApiTests"),
        ["DELETE /api/uploads/{id:guid}"] = new(Access.ScopedOrGuest, "files:write", Execute: false, Note: "creator-only session; FilesApiTests"),
        ["POST /api/uploads/{id:guid}/complete"] = new(Access.ScopedOrGuest, "files:write", Execute: false, Note: "creator-only session; FilesApiTests"),
        ["POST /api/uploads/simple"] = new(Access.ScopedOrGuest, "files:write", Execute: false, Note: "one-request upload, same rules as tus plus complete; FilesS2Tests"),
        ["GET /api/resources/"] = new(Access.Scoped, "resources:read", Execute: false, Note: "owner library; FilesApiTests"),
        ["GET /api/resources/{id:guid}"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "owner, other user (404); FilesApiTests"),
        ["PATCH /api/resources/{id:guid}"] = new(Access.Scoped, "files:write", Resource.OwnerLink, Execute: false, Note: "owner, other user (404), scope; FilesApiTests"),
        ["DELETE /api/resources/{id:guid}"] = new(Access.Scoped, "files:write", Resource.OwnerLink, Execute: false, Note: "owner, other user (404), scope; FilesApiTests"),
        ["GET /api/resources/{id:guid}/status"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "FilesApiTests"),
        ["POST /api/resources/{id:guid}/inline"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "FilesApiTests"),
        ["POST /api/resources/{id:guid}/download"] = new(Access.Scoped, "resources:read", Resource.OwnerLink, Execute: false, Note: "FilesApiTests"),
        ["GET /api/me/storage"] = new(Access.Scoped, "resources:read", Execute: false, Note: "FilesApiTests"),
        ["POST /api/public/content/{code}/download"] = new(Access.Anyone, Execute: false, Note: "visitor download button, gate-checked; FilesApiTests"),
        ["HEAD /internal/delivery/{ticket}"] = new(Access.Anyone, Execute: false, Note: "BE-008: SnippetsApiTests"),

        // ---- Auth (§7.4: POST /auth/* anyone, logout-everywhere authenticated)
        ["POST /api/auth/register"] = new(Access.Anyone, Execute: false, Note: "executed in AuthRoutesTests"),
        ["POST /api/auth/resend-confirmation"] = new(Access.Anyone, Execute: false, Note: "executed in AuthRoutesTests"),
        ["POST /api/auth/confirm-email"] = new(Access.Anyone, Execute: false, Note: "TODO (needs the emailed token; Testing env does not echo it)"),
        ["POST /api/auth/forgot-password"] = new(Access.Anyone, Execute: false, Note: "executed in AuthRoutesTests"),
        ["POST /api/auth/reset-password"] = new(Access.Anyone, Execute: false, Note: "TODO (needs the emailed token)"),
        ["POST /api/auth/login"] = new(Access.Anyone, Execute: false, Note: "executed in AuthRoutesTests (login, refresh, logout flow)"),
        ["POST /api/auth/refresh"] = new(Access.Anyone, Execute: false, Note: "executed in AuthRoutesTests"),
        ["POST /api/auth/logout"] = new(Access.Anyone, Execute: false, Note: "executed in AuthRoutesTests"),
        ["POST /api/auth/logout-everywhere"] = new(Access.JwtOnly, Execute: false, Note: "R1; would log the world's owner out: executed in AuthRoutesTests with throwaway users"),
        ["POST /api/auth/totp/enroll"] = new(Access.JwtOnly, Execute: false, Note: "AZ-01: executed in AuthRoutesTests (enrol-only token accepted)"),
        ["POST /api/auth/totp/confirm"] = new(Access.JwtOnly, Execute: false, Note: "AZ-01: executed in AuthRoutesTests (enrol-only token accepted)"),
        ["GET /api/auth/me"] = new(Access.JwtOnly, Note: "R1"),
        ["GET /api/auth/whoami"] = new(Access.JwtOnly, Note: "ARC-014 3.1: also accepts device tokens (DeviceFlowApiTests)"),
        ["GET /api/auth/external/{provider}"] = new(Access.Anyone, Execute: false, Note: "TODO"),
        ["GET /api/auth/external/callback"] = new(Access.Anyone, Execute: false, Note: "PD-41: executed in Pd41TokenExposureTests (fake External scheme)"),
        ["POST /api/auth/external/exchange"] = new(Access.Anyone, Execute: false, Note: "PD-41: executed in Pd41TokenExposureTests (needs a callback code)"),
        ["POST /api/auth/hub-ticket"] = new(Access.JwtOnly, Note: "PD-41: JWT session only; also Pd41TokenExposureTests"),
        // BE-024 device flow: executed in DeviceFlowApiTests.
        ["POST /api/auth/device/code"] = new(Access.Anyone, Execute: false, Note: "BE-024: executed in DeviceFlowApiTests"),
        ["POST /api/auth/device/token"] = new(Access.Anyone, Execute: false, Note: "BE-024: executed in DeviceFlowApiTests"),
        ["GET /api/auth/device/activation"] = new(Access.JwtOnly, Execute: false, Note: "BE-024: executed in DeviceFlowApiTests (a device token gets 403)"),
        ["POST /api/auth/device/activate"] = new(Access.JwtOnly, Execute: false, Note: "BE-024: executed in DeviceFlowApiTests"),
        ["GET /api/me/sessions/"] = new(Access.JwtOnly, Execute: false, Note: "BE-024: executed in DeviceFlowApiTests"),
        ["DELETE /api/me/sessions/{id:guid}"] = new(Access.JwtOnly, Execute: false, Note: "BE-024: executed in DeviceFlowApiTests"),

        // ---- Links (§7.4 rows)
        ["POST /api/links/"] = new(Access.ScopedOrGuest, "links:write", Body: NewLink),
        ["GET /api/links/"] = new(Access.Scoped, "links:read"),
        ["GET /api/links/{id:guid}"] = new(Access.Scoped, "links:read", Resource.OwnerLink),
        ["PUT /api/links/{id:guid}"] = new(Access.Scoped, "links:write", Resource.OwnerLink, Body: NewLink),
        ["PATCH /api/links/{id:guid}"] = new(Access.Scoped, "links:write", Resource.OwnerLink, Body: J(new { publicStats = false })),
        ["DELETE /api/links/{id:guid}"] = new(Access.Scoped, "links:write", Resource.OwnerLink),
        ["POST /api/links/{id:guid}/renew"] = new(Access.Scoped, "links:write", Resource.OwnerLink,
            Body: () => System.Text.Json.JsonSerializer.Serialize(new { expiresAt = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(20) }, TestPrincipals.Json)),
        ["GET /api/links/slug-available"] = new(Access.Scoped, "links:read", Note: "scope inferred (not in §7.4 table)"),
        ["POST /api/links/bulk"] = new(Access.Scoped, "links:write", Note: "no file sent: an authorized caller gets 400"),
        ["GET /api/tags"] = new(Access.Scoped, "links:read", Note: "scope inferred"),
        ["GET /api/folders/"] = new(Access.Scoped, "links:read", Note: "scope inferred"),
        ["POST /api/folders/"] = new(Access.Scoped, "links:write", Body: () => $"{{\"name\":\"qa-authz-{TestIps.UniqueSuffix()}\"}}", Note: "scope inferred"),
        ["PUT /api/folders/{id:guid}"] = new(Access.Scoped, "links:write", Resource.OwnerFolder, Body: () => $"{{\"name\":\"qa-authz-{TestIps.UniqueSuffix()}\"}}", Note: "scope inferred"),
        ["DELETE /api/folders/{id:guid}"] = new(Access.Scoped, "links:write", Resource.OwnerFolder, Note: "scope inferred"),

        // ---- Analytics (§7.4: stats routes owner with stats:read, or Admin)
        ["GET /api/links/{id:guid}/analytics"] = new(Access.Scoped, "stats:read", Resource.OwnerLink),
        ["GET /api/links/{id:guid}/analytics/events"] = new(Access.Scoped, "stats:read", Resource.OwnerLink),
        ["GET /api/links/{id:guid}/analytics/utm"] = new(Access.Scoped, "stats:read", Resource.OwnerLink),
        ["GET /api/analytics/overview"] = new(Access.Scoped, "stats:read"),
        ["GET /api/archive"] = new(Access.Scoped, "stats:read"),
        ["GET /api/archive/{linkId:guid}/stats"] = new(Access.Scoped, "stats:read", AllowedMayBe404: true, Note: "no archived rows exist: an authorized caller gets 404"),

        // ---- API keys (§7.4 /keys*: the user themself, JWT only)
        ["GET /api/api-keys/"] = new(Access.JwtOnly),
        ["POST /api/api-keys/"] = new(Access.JwtOnly, Body: J(new { name = "qa-authz", scopes = new[] { "links:read" } })),
        ["DELETE /api/api-keys/{id:guid}"] = new(Access.JwtOnly, AllowedMayBe404: true, Note: "random id: an authorized caller gets 404"),

        // ---- Account and billing (JWT only)
        ["DELETE /api/me/"] = new(Access.JwtOnly, Execute: false, Note: "deletes the account; TODO with a throwaway user"),
        ["GET /api/me/export"] = new(Access.JwtOnly, Execute: false, Note: "TODO (1/hour limiter)"),
        ["GET /api/billing/me"] = new(Access.JwtOnly, Note: "R1 (read-only plan status)"),
        ["POST /api/billing/checkout"] = new(Access.JwtOnly, Execute: false, Note: "TODO (Stripe unconfigured)"),
        ["POST /api/billing/portal"] = new(Access.JwtOnly, Execute: false, Note: "TODO (Stripe unconfigured)"),

        // ---- Admin (§7.4 /admin/*). Safe reads run for every principal; writes run only for principals
        // that must be refused (their handler is never reached), never for the in-network admin.
        ["GET /api/admin/users"] = new(Access.Admin),
        ["GET /api/admin/links/{code}"] = new(Access.Admin),
        ["GET /api/admin/reports"] = new(Access.Admin),
        ["GET /api/admin/blocked-destinations"] = new(Access.Admin),
        ["GET /api/admin/blocked-aliases"] = new(Access.Admin),
        ["GET /api/admin/alias-denylist"] = new(Access.Admin),
        ["GET /api/admin/alias-freezes"] = new(Access.Admin),
        ["GET /api/admin/allocator"] = new(Access.Admin, Execute: false, Note: "proxies the coordinator; TODO"),
        ["POST /api/admin/users/{id:guid}/disable"] = new(Access.Admin, Body: J(new { reason = "qa", disableLinks = false }), Note: "negatives only"),
        ["POST /api/admin/users/{id:guid}/enable"] = new(Access.Admin, Body: J(new { restoreLinks = false }), Note: "negatives only"),
        ["POST /api/admin/links/{code}/disable"] = new(Access.Admin, Body: J(new { reason = "qa" }), Note: "negatives only"),
        ["POST /api/admin/links/{code}/enable"] = new(Access.Admin, Note: "negatives only"),
        ["POST /api/admin/links/{code}/reinstate"] = new(Access.Admin, Body: J(new { reason = "qa" }), Note: "negatives only"),
        ["POST /api/admin/takedowns"] = new(Access.Admin, Body: J(new { }), Note: "negatives only"),
        ["POST /api/admin/reports/{id:guid}/resolve"] = new(Access.Admin, Body: J(new { }), Note: "negatives only"),
        ["POST /api/admin/blocked-destinations"] = new(Access.Admin, Body: J(new { }), Note: "negatives only"),
        ["DELETE /api/admin/blocked-destinations/{id:guid}"] = new(Access.Admin, Note: "negatives only"),
        ["POST /api/admin/blocked-aliases"] = new(Access.Admin, Body: J(new { }), Note: "negatives only"),
        ["DELETE /api/admin/blocked-aliases/{code}"] = new(Access.Admin, Note: "negatives only"),
        ["POST /api/admin/alias-denylist"] = new(Access.Admin, Body: J(new { }), Note: "negatives only"),
        ["DELETE /api/admin/alias-denylist/{id:guid}"] = new(Access.Admin, Note: "negatives only"),
        ["POST /api/admin/alias-freezes/{id:guid}/end"] = new(Access.Admin, Body: J(new { }), Note: "negatives only"),
        ["POST /api/admin/declare-restore"] = new(Access.Admin, Execute: false, Note: "global alias freeze: never executed against the shared DB"),
    };

    private static bool IsAdminWrite(string route) => route.Contains("/api/admin/") && !route.StartsWith("GET ");

    /// <summary>Spec §7.4 expectation for one (rule, principal).</summary>
    public static Outcome Expect(Rule rule, string principal)
    {
        var keyScope = principal.StartsWith("key:") ? principal[4..] : null;
        bool KeyHasScope() => keyScope == "all" || keyScope == rule.Scope;

        return rule.Access switch
        {
            Access.Anyone => Outcome.Allowed,
            Access.Scoped or Access.ScopedOrGuest => principal switch
            {
                P.Anonymous => rule.Access == Access.ScopedOrGuest ? Outcome.Allowed : Outcome.Unauthenticated,
                P.OwnerJwt => Outcome.Allowed,
                // PD-40: an admin is a non-owner here, exactly like another user (404 on someone else's resource).
                P.OtherJwt or P.AdminJwt or P.AdminOutsideNetwork => rule.Resource == Resource.None ? Outcome.Allowed : Outcome.NotFound,
                P.AdminEnrolOnly => Outcome.Denied,
                _ => KeyHasScope() ? Outcome.Allowed : Outcome.Forbidden,
            },
            Access.JwtOnly => principal switch
            {
                P.Anonymous => Outcome.Unauthenticated,
                P.OwnerJwt or P.OtherJwt or P.AdminJwt or P.AdminOutsideNetwork => Outcome.Allowed,
                P.AdminEnrolOnly => Outcome.Denied,
                _ => Outcome.Unauthenticated, // R1 / §7.4: an API key on a JWT-only route gets 401
            },
            Access.Admin => principal switch
            {
                P.Anonymous => Outcome.Unauthenticated,
                P.AdminJwt => Outcome.Allowed,
                P.AdminOutsideNetwork => Outcome.Forbidden,
                P.OwnerJwt or P.OtherJwt => Outcome.Forbidden,
                _ => Outcome.Denied,
            },
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    public static Family Classify(Rule rule, string principal)
    {
        if (principal == P.AdminEnrolOnly && rule.Access is not (Access.Admin or Access.Anyone)) return Family.EnrolOnlyAz01;
        if (principal.StartsWith("key:") && rule.Access is Access.Scoped or Access.ScopedOrGuest && Expect(rule, principal) == Outcome.Forbidden)
            return Family.ScopeBe001;
        if (principal is P.AdminJwt or P.AdminOutsideNetwork && rule.Access is Access.Scoped && rule.Resource != Resource.None)
            return Family.AdminOnOwnerRoutes;
        return Family.Core;
    }

    public static TheoryData<string, string> Cases(Family family)
    {
        var data = new TheoryData<string, string>();
        foreach (var (route, rule) in Rules.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!rule.Execute) continue;
            foreach (var principal in P.All)
            {
                // Never let a principal that should SUCCEED reach an admin write handler on the shared DB.
                if (IsAdminWrite(route) && Expect(rule, principal) == Outcome.Allowed) continue;
                // Admin-outside-network only matters on admin routes and owner routes.
                if (principal == P.AdminOutsideNetwork && rule.Access is not (Access.Admin or Access.Scoped)) continue;
                if (Classify(rule, principal) == family) data.Add(route, principal);
            }
        }
        return data;
    }
}

/// <summary>The principals and shared fixtures of the matrix, created once per test run through the product services.</summary>
public sealed class World
{
    public required TestUser Owner { get; init; }
    public required TestUser Other { get; init; }
    public required TestUser Admin { get; init; }
    public required TestUser EnrolOnly { get; init; }
    public required Dictionary<string, string> Keys { get; init; }
    public required LinkDto SharedLink { get; init; }

    private static Task<World>? _instance;
    private static readonly Lock Gate = new();

    public static Task<World> GetAsync(ZaaglApiFactory api)
    {
        lock (Gate) return _instance ??= CreateAsync(api);
    }

    public static IPAddress AdminIp()
    {
        var b = TestIps.RandomV4().GetAddressBytes();
        return new IPAddress(new byte[] { 10, 200, b[2], b[3] });
    }

    private static async Task<World> CreateAsync(ZaaglApiFactory api)
    {
        var p = api.Principals;
        var owner = await p.CreateUserAsync(tag: "authz-owner");
        var keys = new Dictionary<string, string>
        {
            ["links:read"] = await p.CreateApiKeyAsync(owner, "links:read"),
            ["links:write"] = await p.CreateApiKeyAsync(owner, "links:write"),
            ["stats:read"] = await p.CreateApiKeyAsync(owner, "stats:read"),
            ["all"] = await p.CreateApiKeyAsync(owner, "links:read", "links:write", "stats:read"),
        };
        var shared = await p.CreateLinkAsync(owner, new LinkInput($"https://example.com/qa/authz/shared/{TestIps.UniqueSuffix()}", PublicStats: true));
        return new World
        {
            Owner = owner,
            Other = await p.CreateUserAsync(tag: "authz-other"),
            Admin = await p.CreateAdminAsync(),
            EnrolOnly = await p.CreateAdminEnrolOnlyAsync(),
            Keys = keys,
            SharedLink = shared,
        };
    }
}
