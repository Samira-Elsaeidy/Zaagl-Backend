using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Api.Tests;

/// <summary>The snippets harness (user-content host, ticket keys) with the scripted CAPTCHA verifier, so the guest-creation
/// control panel's CAPTCHA toggle can be observed: no token -> captcha-required, any other token passes.</summary>
public sealed class GuestControlsApiFactory : SnippetsApiFactory
{
    public ScriptedCaptchaVerifier Captcha { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaVerifier>();
            services.AddSingleton<ICaptchaVerifier>(Captcha);
        });
    }
}

/// <summary>The guest settings row is global to the database: these tests run alone (no other collection in this process at
/// the same time) and put the row back to "never saved" (configuration defaults) when they finish.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GuestControlsCollection
{
    public const string Name = "Guest controls (global settings row)";
}

/// <summary>
/// The CAPTCHA / guest-creation control panel (PHASE1_STATUS last Phase 1 item): /api/admin/guest-controls authorization
/// (spec §7.4: role Admin + TOTP JWT + Admin:AllowedNetworks), validation, pause and CAPTCHA toggles enforced by the guest
/// create routes at request time, account-route CAPTCHA unaffected, audit rows and stats.
/// </summary>
[Collection(GuestControlsCollection.Name)]
[Trait("Area", "GuestControls")]
public sealed class GuestControlsTests(GuestControlsApiFactory api) : IClassFixture<GuestControlsApiFactory>, IAsyncLifetime
{
    private const string Base = "/api/admin/guest-controls";
    private static readonly JsonSerializerOptions Json = TestPrincipals.Json;

    public Task InitializeAsync() => ResetAsync();
    public Task DisposeAsync() => ResetAsync();

    /// <summary>Back to "never saved": every guest feature on, CAPTCHA required everywhere, configuration limits.</summary>
    private async Task ResetAsync()
    {
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            await db.GuestSettings.ExecuteDeleteAsync();
        }
        api.Services.GetRequiredService<GuestSettingsStore>().Invalidate();
    }

    private TestUser? _admin;
    private async Task<HttpClient> AdminAsync()
    {
        _admin ??= await api.Principals.CreateAdminAsync();
        return api.ApiClient(World.AdminIp(), bearer: _admin.AccessToken);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            Assert.Fail($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} -> {(int)response.StatusCode} (expected {(int)expected}): {await response.Content.ReadAsStringAsync()}");
    }

    private static object Defaults(bool snippetEnabled = true, bool snippetCaptcha = true, int perMinute = 5, int contentPerHour = 30, int contentPerDay = 60, int? version = null) => new
    {
        link = new { enabled = true, captchaRequired = true },
        upload = new { enabled = true, captchaRequired = true },
        snippet = new { enabled = snippetEnabled, captchaRequired = snippetCaptcha },
        limits = new { perMinute, globalPerHour = 5000, contentPerHour, contentPerDay, linksPerDay = 20 },
        version,
        reason = "qa",
    };

    private async Task<JsonElement> PutAsync(HttpClient admin, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.PutAsJsonAsync(Base, body, Json);
        await AssertStatusAsync(response, expected);
        return await BodyAsync(response);
    }

    private HttpClient NewGuest() => api.ApiClient(); // random IPv4 partition: the per-minute guest brake never trips

    private async Task<HttpResponseMessage> CreateGuestSnippetAsync(string? captchaToken)
    {
        using var guest = NewGuest();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/snippets")
        {
            Content = JsonContent.Create(new { content = "guest controls " + TestIps.UniqueSuffix() }, options: Json),
        };
        request.Headers.Add("Idempotency-Key", "k-" + TestIps.UniqueSuffix());
        if (captchaToken is not null) request.Headers.Add("Captcha-Token", captchaToken);
        return await guest.SendAsync(request);
    }

    private async Task<HttpResponseMessage> CreateGuestLinkAsync(string? captchaToken)
    {
        using var guest = NewGuest();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new LinkInput($"https://example.com/qa/guest-controls/{TestIps.UniqueSuffix()}"), options: Json),
        };
        if (captchaToken is not null) request.Headers.Add("Captcha-Token", captchaToken);
        return await guest.SendAsync(request);
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? title = null)
    {
        await AssertStatusAsync(response, status);
        var body = await BodyAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        if (title is not null) Assert.Equal(title, body.GetProperty("title").GetString());
    }

    // ---- Authorization (spec §7.4) ------------------------------------------------------------------------------

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", Base },
        { "PUT", Base },
        { "POST", Base + "/pause-all" },
        { "POST", Base + "/resume-all" },
        { "GET", Base + "/stats" },
        { "GET", Base + "/audit" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET") request.Content = JsonContent.Create(Defaults(snippetEnabled: false), options: Json);
        return request;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Only_an_in_network_admin_with_a_TOTP_session_reaches_the_panel(string method, string path)
    {
        var user = await api.Principals.CreateUserAsync(tag: "gc-user");
        var enrolOnly = await api.Principals.CreateAdminEnrolOnlyAsync();
        _admin ??= await api.Principals.CreateAdminAsync();

        using (var anonymous = api.ApiClient())
            await AssertStatusAsync(await anonymous.SendAsync(Request(method, path)), HttpStatusCode.Unauthorized);
        using (var normal = api.ApiClient(bearer: user.AccessToken))
            await AssertStatusAsync(await normal.SendAsync(Request(method, path)), HttpStatusCode.Forbidden);
        using (var outside = api.ApiClient(TestIps.RandomV4(), bearer: _admin.AccessToken))
            await AssertStatusAsync(await outside.SendAsync(Request(method, path)), HttpStatusCode.Forbidden);
        using (var noTotp = api.ApiClient(World.AdminIp(), bearer: enrolOnly.AccessToken))
        {
            var status = (await noTotp.SendAsync(Request(method, path))).StatusCode;
            Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"enrol-only token -> {(int)status}");
        }

        // Nothing changed: every refused write left the settings at their defaults.
        using var admin = await AdminAsync();
        var settings = (await BodyAsync(await admin.GetAsync(Base))).GetProperty("settings");
        Assert.True(settings.GetProperty("snippet").GetProperty("enabled").GetBoolean());
        Assert.Equal(0, settings.GetProperty("version").GetInt32());
    }

    // ---- Settings document and validation -----------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_config_defaults_until_saved_and_says_account_captcha_is_always_on()
    {
        using var admin = await AdminAsync();
        using var response = await admin.GetAsync(Base);
        await AssertStatusAsync(response, HttpStatusCode.OK);
        var body = await BodyAsync(response);

        var settings = body.GetProperty("settings");
        foreach (var feature in new[] { "link", "upload", "snippet" })
        {
            Assert.True(settings.GetProperty(feature).GetProperty("enabled").GetBoolean());
            Assert.True(settings.GetProperty(feature).GetProperty("captchaRequired").GetBoolean());
        }
        Assert.Equal(0, settings.GetProperty("version").GetInt32());
        Assert.Equal(5, settings.GetProperty("limits").GetProperty("perMinute").GetInt32());
        Assert.Equal(30, settings.GetProperty("limits").GetProperty("contentPerHour").GetInt32());
        Assert.Equal(60, settings.GetProperty("limits").GetProperty("contentPerDay").GetInt32());
        Assert.True(body.GetProperty("accountCaptchaAlwaysOn").GetBoolean());
        Assert.True(body.GetProperty("captchaConfigured").GetBoolean());
        Assert.Equal(1, body.GetProperty("bounds").GetProperty("perMinute").GetProperty("min").GetInt32());
    }

    public static TheoryData<string, object> InvalidBodies => new()
    {
        { "perMinute 0", Defaults(perMinute: 0) },
        { "perMinute too high", Defaults(perMinute: 1_001) },
        { "contentPerDay below contentPerHour", Defaults(contentPerHour: 50, contentPerDay: 40) },
        { "contentPerHour 0", Defaults(contentPerHour: 0) },
        { "missing snippet", new { link = new { enabled = true, captchaRequired = true }, upload = new { enabled = true, captchaRequired = true },
            limits = new { perMinute = 5, globalPerHour = 5000, contentPerHour = 30, contentPerDay = 60, linksPerDay = 20 } } },
        { "missing limits", new { link = new { enabled = true, captchaRequired = true }, upload = new { enabled = true, captchaRequired = true },
            snippet = new { enabled = true, captchaRequired = true } } },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Put_refuses_out_of_range_or_incomplete_settings_and_changes_nothing(string why, object body)
    {
        using var admin = await AdminAsync();
        using var response = await admin.PutAsJsonAsync(Base, body, Json);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{why}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(0, (await BodyAsync(await admin.GetAsync(Base))).GetProperty("settings").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Put_with_a_stale_version_is_409_and_a_save_bumps_the_version()
    {
        using var admin = await AdminAsync();
        var saved = await PutAsync(admin, Defaults(perMinute: 7, version: 0));
        Assert.Equal(1, saved.GetProperty("settings").GetProperty("version").GetInt32());
        Assert.Equal(7, saved.GetProperty("settings").GetProperty("limits").GetProperty("perMinute").GetInt32());

        await PutAsync(admin, Defaults(perMinute: 8, version: 0), HttpStatusCode.Conflict);
        var current = (await BodyAsync(await admin.GetAsync(Base))).GetProperty("settings");
        Assert.Equal(7, current.GetProperty("limits").GetProperty("perMinute").GetInt32());
    }

    // ---- Enforcement at request time ----------------------------------------------------------------------------

    [Fact]
    public async Task Pausing_guest_snippets_refuses_them_with_503_guest_creation_paused_and_resuming_lets_them_through()
    {
        using var admin = await AdminAsync();
        await AssertStatusAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.Created);

        await PutAsync(admin, Defaults(snippetEnabled: false));
        await AssertProblemCodeAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.ServiceUnavailable, "guest_creation_paused");

        // Only guests are paused: an account still creates, and the other guest features are untouched.
        var user = await api.Principals.CreateUserAsync(tag: "gc-paused");
        using (var client = api.ApiClient(bearer: user.AccessToken))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/snippets") { Content = JsonContent.Create(new { content = "account" }, options: Json) };
            request.Headers.Add("Idempotency-Key", "k-" + TestIps.UniqueSuffix());
            await AssertStatusAsync(await client.SendAsync(request), HttpStatusCode.Created);
        }
        await AssertStatusAsync(await CreateGuestLinkAsync("ok"), HttpStatusCode.Created);

        // The public status says so, so the website can show "paused" before the visitor types anything.
        using (var anonymous = api.ApiClient())
        {
            var status = await BodyAsync(await anonymous.GetAsync("/api/guest-status"));
            Assert.False(status.GetProperty("snippet").GetProperty("enabled").GetBoolean());
            Assert.True(status.GetProperty("link").GetProperty("enabled").GetBoolean());
        }

        await PutAsync(admin, Defaults(snippetEnabled: true));
        await AssertStatusAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.Created);
    }

    /// <summary>Phase 2 E2.3: uploads into a drop-off portal follow the panel's <c>guest-drop</c> switches (pause, CAPTCHA), independently of
    /// guest uploads. A huge Upload-Length shows the gates passed (413 file-too-large comes from the upload service, after them).</summary>
    [Fact]
    public async Task Drop_portal_uploads_follow_the_guest_drop_switches()
    {
        using var admin = await AdminAsync();
        var owner = await api.Principals.CreateUserAsync(starter: true);
        using var ownerClient = api.ApiClient(bearer: owner.AccessToken);
        using var created = await ownerClient.PostAsJsonAsync("/api/drops", new { title = "panel", @public = true }, Json);
        await AssertStatusAsync(created, HttpStatusCode.Created);
        var code = (await BodyAsync(created)).GetProperty("code").GetString();

        async Task<HttpResponseMessage> Upload(string? captcha)
        {
            using var guest = NewGuest();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/drops/{code}/uploads");
            request.Headers.Add("Tus-Resumable", "1.0.0");
            request.Headers.Add("Upload-Length", "999999999999");
            if (captcha is not null) request.Headers.Add("Captcha-Token", captcha);
            return await guest.SendAsync(request);
        }

        await AssertProblemCodeAsync(await Upload(null), HttpStatusCode.BadRequest, "validation", "captcha-required");
        await AssertStatusAsync(await Upload("ok"), HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal("guest-drop", api.Captcha.Calls.Last().Action);

        object With(object drop) => new
        {
            link = new { enabled = true, captchaRequired = true },
            upload = new { enabled = true, captchaRequired = true },
            snippet = new { enabled = true, captchaRequired = true },
            limits = new { perMinute = 5, globalPerHour = 5000, contentPerHour = 30, contentPerDay = 60, linksPerDay = 20 },
            drop,
            reason = "qa",
        };
        var paused = await PutAsync(admin, With(new { enabled = false, captchaRequired = true }));
        Assert.False(paused.GetProperty("settings").GetProperty("drop").GetProperty("enabled").GetBoolean());
        Assert.True(paused.GetProperty("settings").GetProperty("table").GetProperty("enabled").GetBoolean()); // absent: unchanged
        var refused = await Upload("ok");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        // Guest uploads (another feature) are not paused by it.
        using (var anonymous = api.ApiClient())
        {
            var status = await BodyAsync(await anonymous.GetAsync("/api/guest-status"));
            Assert.False(status.GetProperty("drop").GetProperty("enabled").GetBoolean());
            Assert.True(status.GetProperty("upload").GetProperty("enabled").GetBoolean());
        }

        await PutAsync(admin, With(new { enabled = true, captchaRequired = false }));
        await AssertStatusAsync(await Upload(null), HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Turning_captcha_off_for_guest_snippets_skips_the_check_for_that_feature_only()
    {
        using var admin = await AdminAsync();
        await AssertProblemCodeAsync(await CreateGuestSnippetAsync(null), HttpStatusCode.BadRequest, "validation", "captcha-required");

        await PutAsync(admin, Defaults(snippetCaptcha: false));
        var before = api.Captcha.Calls.Count;
        await AssertStatusAsync(await CreateGuestSnippetAsync(null), HttpStatusCode.Created);
        Assert.Equal(before, api.Captcha.Calls.Count); // the verifier was not even asked

        // Guest links still need their CAPTCHA, and so does registration (account routes are never switchable).
        await AssertProblemCodeAsync(await CreateGuestLinkAsync(null), HttpStatusCode.BadRequest, "validation", "captcha-required");
        using (var anonymous = api.ApiClient())
        {
            var register = await anonymous.PostAsJsonAsync("/api/auth/register",
                new RegisterRequest($"qa-api-gc-{TestIps.UniqueSuffix()}@example.test", $"Qa-{TestIps.UniqueSuffix()}-pw!", "QA"), Json);
            await AssertProblemCodeAsync(register, HttpStatusCode.BadRequest, "validation", "captcha-required");
        }
        using (var anonymous = api.ApiClient())
        {
            var status = await BodyAsync(await anonymous.GetAsync("/api/guest-status"));
            Assert.False(status.GetProperty("snippet").GetProperty("captchaRequired").GetBoolean());
            Assert.True(status.GetProperty("upload").GetProperty("captchaRequired").GetBoolean());
        }

        await PutAsync(admin, Defaults(snippetCaptcha: true));
        await AssertProblemCodeAsync(await CreateGuestSnippetAsync(null), HttpStatusCode.BadRequest, "validation", "captcha-required");
        await AssertProblemCodeAsync(await CreateGuestSnippetAsync(ScriptedCaptchaVerifier.Bad), HttpStatusCode.BadRequest, "validation", "captcha-invalid");
        var call = api.Captcha.Calls.Last();
        Assert.Equal("guest-snippet", call.Action);
    }

    [Fact]
    public async Task Pause_all_stops_every_guest_feature_and_resume_all_brings_them_back()
    {
        using var admin = await AdminAsync();
        try
        {
            using var paused = await admin.PostAsJsonAsync(Base + "/pause-all", new { reason = "qa incident" }, Json);
            await AssertStatusAsync(paused, HttpStatusCode.OK);
            var settings = (await BodyAsync(paused)).GetProperty("settings");
            Assert.False(settings.GetProperty("link").GetProperty("enabled").GetBoolean());
            Assert.False(settings.GetProperty("upload").GetProperty("enabled").GetBoolean());
            Assert.False(settings.GetProperty("snippet").GetProperty("enabled").GetBoolean());
            Assert.True(settings.GetProperty("snippet").GetProperty("captchaRequired").GetBoolean()); // CAPTCHA untouched

            await AssertProblemCodeAsync(await CreateGuestLinkAsync("ok"), HttpStatusCode.ServiceUnavailable, "guest_creation_paused");
            await AssertProblemCodeAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.ServiceUnavailable, "guest_creation_paused");
            Assert.True((await BodyAsync(await admin.GetAsync(Base + "/stats"))).GetProperty("allPaused").GetBoolean());
        }
        finally
        {
            using var resumed = await admin.PostAsJsonAsync(Base + "/resume-all", new { reason = "qa resolved" }, Json);
            await AssertStatusAsync(resumed, HttpStatusCode.OK);
        }

        await AssertStatusAsync(await CreateGuestLinkAsync("ok"), HttpStatusCode.Created);
        await AssertStatusAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.Created);
        Assert.False((await BodyAsync(await admin.GetAsync(Base + "/stats"))).GetProperty("allPaused").GetBoolean());
    }

    [Fact]
    public async Task A_lower_content_limit_applies_at_once()
    {
        using var admin = await AdminAsync();
        await PutAsync(admin, Defaults(contentPerHour: 1, contentPerDay: 1));

        // One IP partition: the first guest snippet is admitted, the second hits the run-time limit of 1.
        using var guest = api.ApiClient(TestIps.RandomV4());
        async Task<HttpResponseMessage> Create()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/snippets") { Content = JsonContent.Create(new { content = "limit " + TestIps.UniqueSuffix() }, options: Json) };
            request.Headers.Add("Idempotency-Key", "k-" + TestIps.UniqueSuffix());
            request.Headers.Add("Captcha-Token", "ok");
            return await guest.SendAsync(request);
        }
        await AssertStatusAsync(await Create(), HttpStatusCode.Created);
        await AssertStatusAsync(await Create(), HttpStatusCode.TooManyRequests);
    }

    // ---- Audit and stats ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_change_is_audited_with_who_when_old_and_new_and_a_no_op_save_writes_nothing()
    {
        using var admin = await AdminAsync();
        var startedAt = DateTime.UtcNow.AddMinutes(-1);
        await PutAsync(admin, Defaults(snippetCaptcha: false));
        await PutAsync(admin, Defaults(snippetCaptcha: false)); // same document: no change, no row
        using (var p = await admin.PostAsJsonAsync(Base + "/pause-all", new { reason = "qa audit" }, Json)) await AssertStatusAsync(p, HttpStatusCode.OK);
        using (var r = await admin.PostAsJsonAsync(Base + "/resume-all", new { }, Json)) await AssertStatusAsync(r, HttpStatusCode.OK);

        using var response = await admin.GetAsync(Base + "/audit?limit=20");
        await AssertStatusAsync(response, HttpStatusCode.OK);
        var rows = (await BodyAsync(response)).EnumerateArray().Where(e => e.GetProperty("at").GetDateTime() >= startedAt
            && e.GetProperty("actorEmail").GetString() == _admin!.Email).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["resume-all", "pause-all", "update"], rows.Select(r => r.GetProperty("action").GetString()!).ToArray());

        var update = rows[2];
        Assert.Equal(_admin!.Id, update.GetProperty("actorId").GetGuid());
        var change = Assert.Single(update.GetProperty("changes").EnumerateArray());
        Assert.Equal("guest-snippet.captchaRequired", change.GetProperty("field").GetString());
        Assert.Equal("true", change.GetProperty("old").GetString());
        Assert.Equal("false", change.GetProperty("new").GetString());

        var pause = rows[1];
        Assert.Equal("qa audit", pause.GetProperty("reason").GetString());
        Assert.Equal(5, pause.GetProperty("changes").GetArrayLength()); // link, upload, snippet + Phase 2 E2.3 drop, table
    }

    [Fact]
    public async Task Stats_count_guest_creations_and_refusals()
    {
        using var admin = await AdminAsync();
        static JsonElement Snippet(JsonElement stats) => stats.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("feature").GetString() == "guest-snippet");

        var before = Snippet(await BodyAsync(await admin.GetAsync(Base + "/stats")));
        await AssertStatusAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.Created);
        await AssertProblemCodeAsync(await CreateGuestSnippetAsync(null), HttpStatusCode.BadRequest, "validation");
        await PutAsync(admin, Defaults(snippetEnabled: false));
        await AssertProblemCodeAsync(await CreateGuestSnippetAsync("ok"), HttpStatusCode.ServiceUnavailable, "guest_creation_paused");

        var after = Snippet(await BodyAsync(await admin.GetAsync(Base + "/stats")));
        Assert.True(after.GetProperty("lastHour").GetInt32() >= before.GetProperty("lastHour").GetInt32() + 1);
        Assert.True(after.GetProperty("last24Hours").GetInt32() >= after.GetProperty("lastHour").GetInt32());
        Assert.True(after.GetProperty("last7Days").GetInt32() >= after.GetProperty("last24Hours").GetInt32());
        Assert.Equal(before.GetProperty("captchaFailures").GetInt64() + 1, after.GetProperty("captchaFailures").GetInt64());
        Assert.Equal(before.GetProperty("pausedRefusals").GetInt64() + 1, after.GetProperty("pausedRefusals").GetInt64());
        Assert.False(after.GetProperty("enabled").GetBoolean());
    }
}
