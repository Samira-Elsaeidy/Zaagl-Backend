using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Tests;

/// <summary>BE-024 / ARC-014 / contracts C9.2: RFC 8628 device flow, device token claims, device sessions.</summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "BE-024")]
public sealed partial class DeviceFlowApiTests(ZaaglApiFactory api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$")]
    private static partial Regex UserCodeShape();

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<DeviceCodeResponse> StartAsync(string clientId = NativeClientIds.Cli, string? name = "My laptop", string? os = "linux")
    {
        using var c = api.ApiClient();
        var r = await c.PostAsJsonAsync("/api/auth/device/code", new { client_id = clientId, device_name = name, os });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<DeviceCodeResponse>(Web))!;
    }

    private async Task<HttpResponseMessage> PollAsync(DeviceCodeResponse code, string clientId = NativeClientIds.Cli, string grant = DeviceFlowWire.GrantType)
    {
        using var c = api.ApiClient();
        return await c.PostAsJsonAsync("/api/auth/device/token", new { grant_type = grant, device_code = code.DeviceCode, client_id = clientId });
    }

    private async Task<HttpResponseMessage> ActivateAsync(TestUser user, string userCode, bool approve)
    {
        using var web = api.ApiClient(bearer: user.AccessToken);
        return await web.PostAsJsonAsync("/api/auth/device/activate", new { userCode, approve });
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        return (await Json(r)).GetProperty("error").GetString()!;
    }

    private async Task<DeviceAuthResponse> SignInDeviceAsync(TestUser user, string clientId = NativeClientIds.Cli)
    {
        var code = await StartAsync(clientId);
        Assert.Equal(HttpStatusCode.NoContent, (await ActivateAsync(user, code.UserCode, true)).StatusCode);
        var poll = await PollAsync(code, clientId);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        return (await poll.Content.ReadFromJsonAsync<DeviceAuthResponse>(Web))!;
    }

    [Fact]
    public async Task Code_response_has_the_rfc8628_shape_and_the_configured_verification_uri()
    {
        using var c = api.ApiClient();
        var r = await c.PostAsJsonAsync("/api/auth/device/code", new { client_id = "zaagl-desktop", device_name = "Desk", os = "windows" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        var j = await Json(r);
        var uri = api.Services.GetRequiredService<IOptions<DeviceAuthOptions>>().Value.VerificationUri;
        Assert.Equal(uri, j.GetProperty("verification_uri").GetString());
        Assert.Matches(UserCodeShape(), j.GetProperty("user_code").GetString());
        Assert.Equal($"{uri}?code={j.GetProperty("user_code").GetString()}", j.GetProperty("verification_uri_complete").GetString());
        Assert.Equal(600, j.GetProperty("expires_in").GetInt32());
        Assert.Equal(5, j.GetProperty("interval").GetInt32());
        Assert.True(j.GetProperty("device_code").GetString()!.Length >= 43);
    }

    [Fact]
    public async Task Code_accepts_a_form_body_and_rejects_unknown_clients_and_bad_input()
    {
        using var c = api.ApiClient();
        var form = await c.PostAsync("/api/auth/device/code", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "zaagl-cli", ["os"] = "mac" }));
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);

        var unknown = await c.PostAsJsonAsync("/api/auth/device/code", new { client_id = "evil" });
        Assert.Equal("invalid_client", await ErrorOf(unknown));

        var tooLong = await c.PostAsJsonAsync("/api/auth/device/code", new { client_id = "zaagl-cli", device_name = new string('x', 65) });
        Assert.Equal("invalid_request", await ErrorOf(tooLong));

        var garbage = await c.PostAsync("/api/auth/device/code", new StringContent("{nope", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal("invalid_request", await ErrorOf(garbage));
    }

    [Fact]
    public async Task Token_errors_follow_rfc6749_pending_slow_down_grant_and_client()
    {
        var code = await StartAsync();
        Assert.Equal("authorization_pending", await ErrorOf(await PollAsync(code)));
        Assert.Equal("slow_down", await ErrorOf(await PollAsync(code))); // immediate second poll

        Assert.Equal("unsupported_grant_type", await ErrorOf(await PollAsync(code, grant: "password")));
        Assert.Equal("invalid_client", await ErrorOf(await PollAsync(code, "zaagl-desktop")));
        Assert.Equal("invalid_client", await ErrorOf(await PollAsync(code, "nope")));

        var bogus = code with { DeviceCode = "not-a-real-device-code" };
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(bogus)));
    }

    [Fact]
    public async Task Approved_code_yields_a_device_token_pair_once_with_the_device_claims()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-happy");
        var code = await StartAsync(NativeClientIds.Desktop, "Desk‮\u0007top", "windows");

        using var web = api.ApiClient(bearer: user.AccessToken);
        var info = await web.GetAsync($"/api/auth/device/activation?userCode={code.UserCode.ToLowerInvariant().Replace("-", "")}");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        var ij = await Json(info);
        Assert.Equal(code.UserCode, ij.GetProperty("userCode").GetString());
        Assert.Equal("zaagl-desktop", ij.GetProperty("clientId").GetString());
        Assert.Equal("Desktop", ij.GetProperty("deviceName").GetString()); // control and bidi characters stripped
        Assert.Equal("windows", ij.GetProperty("os").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await ActivateAsync(user, code.UserCode, true)).StatusCode);

        var poll = await PollAsync(code, NativeClientIds.Desktop);
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Contains("no-store", poll.Headers.CacheControl?.ToString());
        var auth = (await poll.Content.ReadFromJsonAsync<DeviceAuthResponse>(Web))!;
        Assert.Null(auth.User.Role);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        Assert.Equal("zaagl-desktop", jwt.Claims.Single(c => c.Type == NativeClientClaims.ClientId).Value);
        Assert.Equal("device", jwt.Claims.Single(c => c.Type == NativeClientClaims.Amr).Value);
        Assert.Equal(auth.SessionId.ToString(), jwt.Claims.Single(c => c.Type == NativeClientClaims.SessionId).Value);
        Assert.Contains(jwt.Claims, c => c.Type == "sstamp");
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "role" or System.Security.Claims.ClaimTypes.Role);
        Assert.Equal(
            new[] { "files:write", "links:read", "links:write", "resources:read", "snippets:write", "stats:read" },
            jwt.Claims.Where(c => c.Type == "scope").Select(c => c.Value).Order().ToArray());

        // A second redeem of the same device code is invalid_grant (single use).
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(code, NativeClientIds.Desktop)));

        // The user code is single use too.
        Assert.Equal(HttpStatusCode.NotFound, (await ActivateAsync(user, code.UserCode, true)).StatusCode);
    }

    [Fact]
    public async Task Denied_code_returns_access_denied_then_invalid_grant()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-deny");
        var code = await StartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ActivateAsync(user, code.UserCode, approve: false)).StatusCode);
        Assert.Equal("access_denied", await ErrorOf(await PollAsync(code)));
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(code)));
    }

    [Fact]
    public async Task Unknown_user_code_is_404_and_activation_needs_a_web_session()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-404");
        Assert.Equal(HttpStatusCode.NotFound, (await ActivateAsync(user, "BCDF-GHJK", true)).StatusCode);

        using var anon = api.ApiClient();
        var r = await anon.PostAsJsonAsync("/api/auth/device/activate", new { userCode = "BCDF-GHJK", approve = true });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);

        var device = await SignInDeviceAsync(user);
        using var native = api.ApiClient(bearer: device.AccessToken);
        var denied = await native.PostAsJsonAsync("/api/auth/device/activate", new { userCode = "BCDF-GHJK", approve = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); // a device cannot approve another device
    }

    [Fact]
    public async Task Admin_can_approve_but_the_device_token_never_carries_the_admin_role()
    {
        var admin = await api.Principals.CreateAdminAsync();
        var device = await SignInDeviceAsync(admin);
        Assert.Null(device.User.Role);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(device.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "role" or System.Security.Claims.ClaimTypes.Role);
        using var native = api.ApiClient(bearer: device.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await native.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Device_token_works_on_scoped_routes_but_is_refused_by_interactive_routes()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-scope");
        var device = await SignInDeviceAsync(user);
        using var native = api.ApiClient(bearer: device.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await native.GetAsync("/api/links")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await native.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await native.GetAsync("/api/me/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await native.GetAsync("/api/api-keys")).StatusCode);
    }

    [Fact]
    public async Task Whoami_accepts_a_device_token_and_hides_the_role()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-whoami");
        var device = await SignInDeviceAsync(user);
        using var native = api.ApiClient(bearer: device.AccessToken);
        var r = await native.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(user.Email, doc.RootElement.GetProperty("email").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("role").ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("emailConfirmed", out _));
    }

    [Fact]
    public async Task Device_sessions_are_listed_refresh_stays_a_device_token_and_revoke_ends_it()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-sessions");
        var other = await api.Principals.CreateUserAsync(tag: "dev-sessions-other");
        var device = await SignInDeviceAsync(user, NativeClientIds.Desktop);
        var otherDevice = await SignInDeviceAsync(other);

        using var web = api.ApiClient(bearer: user.AccessToken);
        var list = await web.GetFromJsonAsync<DeviceSessionDto[]>("/api/me/sessions", Web);
        var mine = Assert.Single(list!);
        Assert.Equal(device.SessionId, mine.SessionId);
        Assert.Equal("zaagl-desktop", mine.ClientId);
        Assert.Equal("My laptop", mine.DeviceName);

        // Refresh keeps the device claims and the session id, and marks the session as used.
        using var anon = api.ApiClient();
        var refreshed = await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(device.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var rotated = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>(Web))!;
        Assert.Null(rotated.User.Role);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(rotated.AccessToken);
        Assert.Equal("device", jwt.Claims.Single(c => c.Type == NativeClientClaims.Amr).Value);
        Assert.Equal(device.SessionId.ToString(), jwt.Claims.Single(c => c.Type == NativeClientClaims.SessionId).Value);
        Assert.Equal(DateTime.UtcNow.AddDays(30).Date, rotated.RefreshTokenExpiresAt.Date); // 30-day sliding
        Assert.NotNull((await web.GetFromJsonAsync<DeviceSessionDto[]>("/api/me/sessions", Web))![0].LastUsedAt);

        // Someone else's session is a 404 and stays alive.
        Assert.Equal(HttpStatusCode.NotFound, (await web.DeleteAsync($"/api/me/sessions/{otherDevice.SessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.DeleteAsync($"/api/me/sessions/{Guid.NewGuid()}")).StatusCode);

        using var native = api.ApiClient(bearer: rotated.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await native.GetAsync("/api/links")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await web.DeleteAsync($"/api/me/sessions/{device.SessionId}")).StatusCode);
        Assert.Empty((await web.GetFromJsonAsync<DeviceSessionDto[]>("/api/me/sessions", Web))!);

        // The refresh token and the access token both stop working.
        var afterRefresh = await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await native.GetAsync("/api/links")).StatusCode);

        // The other user's session is untouched.
        using var otherNative = api.ApiClient(bearer: otherDevice.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await otherNative.GetAsync("/api/links")).StatusCode);
    }

    [Fact]
    public async Task Logout_everywhere_revokes_device_sessions()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-logout-all");
        var device = await SignInDeviceAsync(user);
        using var web = api.ApiClient(bearer: user.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/auth/logout-everywhere", null)).StatusCode);
        using var anon = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(device.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Wrong_user_codes_lock_the_user_out_even_for_a_valid_code()
    {
        var user = await api.Principals.CreateUserAsync(tag: "dev-lockout");
        var bystander = await api.Principals.CreateUserAsync(tag: "dev-lockout-other");
        var valid = await StartAsync();

        using var scope = api.Services.CreateScope();
        var flow = scope.ServiceProvider.GetRequiredService<DeviceFlowService>();
        var max = scope.ServiceProvider.GetRequiredService<IOptions<DeviceAuthOptions>>().Value.LockoutMaxFailures;

        for (var i = 0; i < max; i++)
        {
            var miss = await flow.ActivateAsync(user.Id, "BCDF-GHJK", true, default);
            Assert.Equal(ErrorCodes.NotFound, miss.Error!.Code);
        }
        var locked = await flow.ActivateAsync(user.Id, valid.UserCode, true, default);
        Assert.Equal(ErrorCodes.TooManyRequests, locked.Error!.Code);
        Assert.Equal(ErrorCodes.TooManyRequests, (await flow.GetActivationAsync(user.Id, valid.UserCode, default)).Error!.Code);

        // Another user is not affected, and the valid code is still pending for them.
        Assert.True((await flow.ActivateAsync(bystander.Id, valid.UserCode, true, default)).IsSuccess);
    }
}
