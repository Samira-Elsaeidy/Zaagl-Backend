using System.Net;
using System.Net.Http.Json;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Identity.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>
/// Executed coverage for the routes the authorization matrix marks Execute:false (Head review stage1-qa.md:
/// "R1 and AZ-01 are not covered by executed cases"): /api/auth/* over HTTP, logout-everywhere with throwaway
/// users (R1, spec §7.4: JWT only), the enrol-only token accepted on /totp/* (AZ-01 positive cases), and the
/// analytics hub negotiate endpoint (spec §7.4 stats:read; BE-001).
/// Every test creates its own users; nothing here touches the matrix's shared World.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "QA-010")]
public sealed class AuthRoutesTests(ZaaglApiFactory api)
{
    private TestPrincipals P => api.Principals;

    private static async Task<string> Body(HttpResponseMessage r)
    {
        var s = await r.Content.ReadAsStringAsync();
        return s.Length <= 300 ? s : s[..300] + "...";
    }

    // ---- Public auth routes -------------------------------------------------------------------------

    [Fact]
    public async Task Register_resend_and_forgot_password_answer_202_with_the_same_shape_for_new_and_existing_addresses()
    {
        var existing = await P.CreateUserAsync(tag: "auth-existing");
        using var client = api.ApiClient();
        var fresh = $"qa-api-auth-new-{TestIps.UniqueSuffix()}@example.test";

        var r1 = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(fresh, $"Qa-{TestIps.UniqueSuffix()}-pw!", "QA new"));
        var r2 = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(existing.Email, $"Qa-{TestIps.UniqueSuffix()}-pw!", "QA dup"));
        Assert.True(r1.StatusCode == HttpStatusCode.Accepted, $"register new -> {(int)r1.StatusCode} {await Body(r1)}");
        Assert.True(r2.StatusCode == HttpStatusCode.Accepted, $"register existing -> {(int)r2.StatusCode} {await Body(r2)}");
        Assert.Equal(await r1.Content.ReadAsStringAsync(), await r2.Content.ReadAsStringAsync()); // no account enumeration
        Assert.DoesNotContain("devOnly", await r1.Content.ReadAsStringAsync()); // Testing env: no token echo

        var resend = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new { email = fresh });
        Assert.True(resend.StatusCode == HttpStatusCode.Accepted, $"resend -> {(int)resend.StatusCode} {await Body(resend)}");

        var forgotKnown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = existing.Email });
        var forgotUnknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = $"qa-api-nobody-{TestIps.UniqueSuffix()}@example.test" });
        Assert.Equal(HttpStatusCode.Accepted, forgotKnown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, forgotUnknown.StatusCode);
        Assert.Equal(await forgotKnown.Content.ReadAsStringAsync(), await forgotUnknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_refresh_logout_flow_over_http()
    {
        var user = await P.CreateUserAsync(tag: "auth-flow");
        using var client = api.ApiClient();

        var bad = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, "wrong-password", null));
        Assert.True((int)bad.StatusCode is >= 400 and < 500, $"wrong password -> {(int)bad.StatusCode}");

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password, null));
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"login -> {(int)login.StatusCode} {await Body(login)}");
        var session = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestPrincipals.Json))!;
        Assert.False(string.IsNullOrEmpty(session.AccessToken));
        Assert.False(string.IsNullOrEmpty(session.RefreshToken));

        using (var authed = api.ApiClient(bearer: session.AccessToken))
            Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/auth/me")).StatusCode);

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.True(refresh.StatusCode == HttpStatusCode.OK, $"refresh -> {(int)refresh.StatusCode} {await Body(refresh)}");
        var rotated = (await refresh.Content.ReadFromJsonAsync<AuthResponse>(TestPrincipals.Json))!;
        Assert.NotEqual(session.RefreshToken, rotated.RefreshToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.True((int)afterLogout.StatusCode is >= 400 and < 500, $"refresh after logout -> {(int)afterLogout.StatusCode}");
    }

    // ---- R1: logout-everywhere is JWT only ---------------------------------------------------------

    [Fact]
    [Trait("Rework", "R1")]
    public async Task Logout_everywhere_refuses_anonymous_api_keys_and_the_enrol_only_token_with_401()
    {
        var user = await P.CreateUserAsync(tag: "auth-r1-neg");
        var key = await P.CreateApiKeyAsync(user, "links:read", "links:write", "stats:read");
        var enrolOnly = await P.CreateAdminEnrolOnlyAsync();

        using (var anonymous = api.ApiClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/auth/logout-everywhere", null)).StatusCode);
        using (var withKey = api.ApiClient(apiKey: key))
        {
            var r = await withKey.PostAsync("/api/auth/logout-everywhere", null);
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"API key -> {(int)r.StatusCode} {await Body(r)}");
        }
        using (var withEnrol = api.ApiClient(World.AdminIp(), bearer: enrolOnly.AccessToken))
        {
            var r = await withEnrol.PostAsync("/api/auth/logout-everywhere", null);
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"enrol-only token -> {(int)r.StatusCode} {await Body(r)}");
        }

        // The key's refusal must not have logged the owner out.
        using var owner = api.ApiClient(bearer: user.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    [Trait("Rework", "R1")]
    public async Task Logout_everywhere_with_a_session_jwt_revokes_refresh_tokens()
    {
        var user = await P.CreateUserAsync(tag: "auth-r1-pos");
        using var client = api.ApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password, null));
        var session = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestPrincipals.Json))!;

        using var authed = api.ApiClient(bearer: session.AccessToken);
        var r = await authed.PostAsync("/api/auth/logout-everywhere", null);
        Assert.True(r.StatusCode == HttpStatusCode.NoContent, $"logout-everywhere -> {(int)r.StatusCode} {await Body(r)}");

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.True((int)refresh.StatusCode is >= 400 and < 500, $"refresh after logout-everywhere -> {(int)refresh.StatusCode}");
        // Not asserted: the access token itself. The security stamp is cached 60 s (SecurityStampValidator),
        // so the old access token may keep working for up to a minute by design (spec §7.4 "~60s").
    }

    [Theory]
    [Trait("Rework", "R1")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/api/billing/me")]
    [InlineData("POST", "/api/auth/logout-everywhere")]
    public async Task Jwt_only_routes_answer_401_to_an_api_key_with_every_scope(string method, string path)
    {
        var user = await P.CreateUserAsync(tag: "auth-r1-key");
        var key = await P.CreateApiKeyAsync(user, "links:read", "links:write", "stats:read");
        using var client = api.ApiClient(apiKey: key);
        var r = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} with API key -> {(int)r.StatusCode} {await Body(r)}");
    }

    // ---- AZ-01 positive cases: the enrol-only token works on /totp/* ------------------------------------

    [Fact]
    [Trait("Defect", "AZ-01")]
    public async Task Enrol_only_token_can_enrol_and_confirm_totp_then_admin_can_sign_in_with_a_code()
    {
        var admin = await P.CreateAdminEnrolOnlyAsync();
        using var enrol = api.ApiClient(World.AdminIp(), bearer: admin.AccessToken);

        var start = await enrol.PostAsync("/api/auth/totp/enroll", null);
        Assert.True(start.StatusCode == HttpStatusCode.OK, $"totp/enroll with enrol-only token -> {(int)start.StatusCode} {await Body(start)}");
        var secret = (await start.Content.ReadFromJsonAsync<TotpEnrollResponse>(TestPrincipals.Json))!.AuthenticatorKey;

        var confirm = await enrol.PostAsJsonAsync("/api/auth/totp/confirm", new TotpConfirmRequest(TestTotp.Now(secret)));
        Assert.True(confirm.StatusCode == HttpStatusCode.NoContent, $"totp/confirm with enrol-only token -> {(int)confirm.StatusCode} {await Body(confirm)}");

        // Still refused elsewhere, even after enrolment.
        Assert.Equal(HttpStatusCode.Unauthorized, (await enrol.GetAsync("/api/auth/me")).StatusCode);

        using var anonymous = api.ApiClient(World.AdminIp());
        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(admin.Email, admin.Password, TestTotp.Now(secret)));
        Assert.True(login.StatusCode == HttpStatusCode.OK, $"admin login with TOTP -> {(int)login.StatusCode} {await Body(login)}");
        var session = await login.Content.ReadFromJsonAsync<AuthResponse>(TestPrincipals.Json);
        Assert.False(string.IsNullOrEmpty(session?.AccessToken));
    }

    [Fact]
    [Trait("Defect", "AZ-01")]
    public async Task Totp_routes_refuse_anonymous_and_api_keys()
    {
        var user = await P.CreateUserAsync(tag: "auth-totp-neg");
        var key = await P.CreateApiKeyAsync(user, "links:read", "links:write", "stats:read");
        using var anonymous = api.ApiClient();
        using var withKey = api.ApiClient(apiKey: key);
        foreach (var path in new[] { "/api/auth/totp/enroll", "/api/auth/totp/confirm" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, new { code = "000000" })).StatusCode);
            var r = await withKey.PostAsJsonAsync(path, new { code = "000000" });
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{path} with API key -> {(int)r.StatusCode}");
        }
    }

    // ---- Analytics hub negotiate (spec §7.4 stats:read; BE-001) ------------------------------------------

    private static HttpRequestMessage Negotiate(string? query = null) =>
        new(HttpMethod.Post, "/hubs/analytics/negotiate?negotiateVersion=1" + query);

    [Fact]
    public async Task Hub_negotiate_requires_a_session_jwt_or_a_key_with_stats_read()
    {
        var user = await P.CreateUserAsync(tag: "auth-hub");
        var statsKey = await P.CreateApiKeyAsync(user, "stats:read");
        var linksKey = await P.CreateApiKeyAsync(user, "links:read", "links:write");
        var enrolOnly = await P.CreateAdminEnrolOnlyAsync();

        async Task<HttpStatusCode> Send(HttpClient c) => (await c.SendAsync(Negotiate())).StatusCode;

        using (var c = api.ApiClient()) Assert.Equal(HttpStatusCode.Unauthorized, await Send(c));
        using (var c = api.ApiClient(bearer: user.AccessToken)) Assert.Equal(HttpStatusCode.OK, await Send(c));
        using (var c = api.ApiClient(apiKey: statsKey)) Assert.Equal(HttpStatusCode.OK, await Send(c));
        using (var c = api.ApiClient(apiKey: linksKey)) Assert.Equal(HttpStatusCode.Forbidden, await Send(c));
        using (var c = api.ApiClient(World.AdminIp(), bearer: enrolOnly.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, await Send(c));
        using (var c = api.ApiClient(bearer: "not-a-jwt")) Assert.Equal(HttpStatusCode.Unauthorized, await Send(c));
    }

    [Fact]
    public async Task Hub_negotiate_is_not_served_on_the_short_host()
    {
        using var c = api.ShortClient();
        Assert.Equal((HttpStatusCode)421, (await c.SendAsync(Negotiate())).StatusCode);
    }
}
