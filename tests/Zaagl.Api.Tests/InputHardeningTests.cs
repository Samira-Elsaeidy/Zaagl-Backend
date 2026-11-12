using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Zaagl.Api.Endpoints;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Snippets.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>Raises the per-minute brakes so a sweep of many bad requests is not answered 429.</summary>
public sealed class InputHardeningFactory : SnippetsApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings().Concat(
        [
            new KeyValuePair<string, string>("RateLimits:AuthPerMinute", "100000"),
            new KeyValuePair<string, string>("RateLimits:LinkWritePerMinute", "100000"),
            new KeyValuePair<string, string>("RateLimits:DeviceActivatePerMinute", "100000"),
            new KeyValuePair<string, string>("RateLimits:UnlockPerMinute", "100000"),
            new KeyValuePair<string, string>("Guests:PerMinute", "100000"),
        ]);
}

/// <summary>
/// Manual QA findings: (1) the owner's private link title leaked on the unauthenticated public stats response; (2) a JSON value of the
/// wrong type made request binding throw BadHttpRequestException, which ApiExceptionHandler answered with a 500; (3) 99999999999999 for
/// maxClicks (an int) was the same failure; the other numeric fields get explicit upper bounds.
/// </summary>
[Trait("Area", "Input-hardening")]
public sealed class InputHardeningTests(InputHardeningFactory api) : IClassFixture<InputHardeningFactory>
{
    private static readonly JsonSerializerOptions Json = TestPrincipals.Json;
    private const string Good = "https://example.com/hardening";

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task AssertCleanProblem(HttpResponseMessage r, string what, params string[] allowedCodes)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True((int)r.StatusCode is >= 400 and < 500, $"{what}: expected a 4xx, got {(int)r.StatusCode}: {text}");
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(text);
        var code = doc.RootElement.GetProperty("code").GetString();
        Assert.True(doc.RootElement.TryGetProperty("title", out _) && doc.RootElement.TryGetProperty("status", out _) && doc.RootElement.TryGetProperty("type", out _),
            $"{what}: not a zaa.gl problem: {text}");
        if (allowedCodes.Length > 0) Assert.True(allowedCodes.Contains(code), $"{what}: code '{code}' not in [{string.Join(",", allowedCodes)}]: {text}");
        // No internals: no exception type, no framework message, no stack.
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("Failed to read parameter", text);
        Assert.DoesNotContain("   at ", text);
    }

    // ---- 1. Private title never on a public response -------------------------------------------------------------------------

    [Fact]
    public async Task Public_stats_json_never_contains_the_private_title_or_other_owner_fields()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "pubstats");
        var title = "PRIVATE-TITLE-" + TestIps.UniqueSuffix();
        var note = "PRIVATE-NOTE-" + TestIps.UniqueSuffix();
        var tag = "ptag" + TestIps.UniqueSuffix()[..6];
        var link = await api.Principals.CreateLinkAsync(owner,
            new LinkInput($"https://example.com/dest/{TestIps.UniqueSuffix()}", Title: title, Description: note, Tags: [tag], PublicStats: true));

        using var anon = api.ApiClient();
        var response = await anon.GetAsync($"/api/public/stats/{link.Slug}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(title, text);
        Assert.DoesNotContain(note, text);
        Assert.DoesNotContain(tag, text);
        Assert.DoesNotContain("example.com/dest", text);
        Assert.DoesNotContain(owner.Id.ToString(), text);

        using var doc = JsonDocument.Parse(text);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["overview", "slug"], keys);
        Assert.False(doc.RootElement.TryGetProperty("title", out _));
    }

    /// <summary>Guards the public response shapes: a property whose name suggests an owner-private field cannot be added unnoticed.</summary>
    [Theory]
    [InlineData(typeof(ContentDescriptorDto))]
    [InlineData(typeof(FileBlockDto))]
    [InlineData(typeof(SnippetViewDto))]
    [InlineData(typeof(PublicLink))]
    [InlineData(typeof(UnlockContentResponse))]
    public void Public_response_types_carry_no_owner_private_properties(Type type)
    {
        string[] forbidden = ["tags", "folder", "notes", "owner", "userid", "ip", "useragent", "ua", "reason", "destination", "email", "internal", "visitor"];
        foreach (var property in type.GetProperties())
        {
            var name = property.Name.ToLowerInvariant();
            Assert.False(forbidden.Any(f => name == f || name.StartsWith(f, StringComparison.Ordinal)),
                $"{type.Name}.{property.Name} looks like an owner-private field on a public response.");
        }
        // PublicLink in particular must not carry the library title.
        if (type == typeof(PublicLink)) Assert.Null(type.GetProperty("Title"));
    }

    // ---- 2. Wrong-typed JSON is a clean 400 --------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"destinationUrl\": 12345}")]
    [InlineData("{\"destinationUrl\": true}")]
    [InlineData("{\"destinationUrl\": [\"https://example.com\"]}")]
    [InlineData("{\"destinationUrl\": {\"a\": 1}}")]
    [InlineData("{\"destinationUrl\": 1e999}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"title\": 5}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"tags\": \"abc\"}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"tags\": [1, 2]}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"folderId\": 7}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"folderId\": \"not-a-guid\"}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"publicStats\": \"yes\"}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"isActive\": 1}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": \"x\"}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"maxClicks\": \"abc\"}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"maxClicks\": 1.5}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"maxClicks\": 99999999999999}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"maxClicks\": 2147483648}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"expiresAt\": 12}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"expiresAt\": \"tomorrow\"}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"policy\": {\"password\": 1234}}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"rules\": [{\"kind\": 1, \"url\": \"https://example.com\"}]}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"rules\": [{\"kind\": \"split\", \"url\": \"https://example.com\", \"weight\": 99999999999}]}")]
    [InlineData("{\"destinationUrl\": \"https://example.com\", \"openGraph\": [1]}")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("{\"destinationUrl\": ")]
    [InlineData("not json at all")]
    public async Task Link_create_with_a_wrongly_typed_or_malformed_body_is_a_400_never_a_500(string json)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "bind-links");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsync("/api/links", Body(json));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await AssertCleanProblem(r, json, "invalid_request", "validation");
    }

    [Fact]
    public async Task Wrong_type_error_names_the_field_and_uses_the_stable_code()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "bind-field");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsync("/api/links", Body("{\"destinationUrl\": 12345}"));
        var text = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://zaa.gl/problems/invalid-request", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("destinationUrl", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Missing_body_and_wrong_content_type_are_clean_4xx()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "bind-ct");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var empty = await client.PostAsync("/api/links", new StringContent("", Encoding.UTF8, "application/json"));
        await AssertCleanProblem(empty, "empty body");
        using var text = await client.PostAsync("/api/links", new StringContent("{\"destinationUrl\":\"https://example.com\"}", Encoding.UTF8, "text/plain"));
        // The framework answers a content type it cannot read with a bare 415 (no body); the point is that it is a 4xx, not a 500.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
    }

    [Fact]
    public async Task Bad_route_and_query_values_are_a_clean_400()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "bind-query");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        foreach (var url in new[]
                 {
                     "/api/links?page=abc", "/api/links?pageSize=99999999999", "/api/links?page=1.5", "/api/links?folderId=nope",
                     "/api/links/not-a-guid", $"/api/links/{Guid.NewGuid()}/analytics/events?limit=99999999999",
                 })
        {
            using var r = await client.GetAsync(url);
            Assert.True(r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{url} -> {(int)r.StatusCode}");
            Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
        }
        using var bad = await client.GetAsync("/api/links?page=abc");
        await AssertCleanProblem(bad, "page=abc", "invalid_request");
    }

    [Fact]
    public async Task A_page_number_near_int_max_does_not_overflow_the_offset()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "bind-page");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.GetAsync("/api/links?page=2147483647&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    /// <summary>The sweep: every body-taking endpoint, wrong-typed bodies. Each must be a 4xx problem+json (400 where the caller can reach the handler).</summary>
    [Theory]
    // links / folders
    [InlineData("PUT", "/api/links/{id}", "{\"destinationUrl\": 5}", "user")]
    [InlineData("PUT", "/api/links/{id}", "{\"destinationUrl\": \"https://example.com\", \"policy\": {\"maxClicks\": 99999999999999}}", "user")]
    [InlineData("PATCH", "/api/links/{id}", "{\"isActive\": \"x\"}", "user")]
    [InlineData("PATCH", "/api/links/{id}", "{\"publicStats\": 5}", "user")]
    [InlineData("POST", "/api/links/{id}/renew", "{\"expiresAt\": 123}", "user")]
    [InlineData("POST", "/api/links/{id}/renew", "{\"expiresAt\": [1]}", "user")]
    [InlineData("POST", "/api/folders", "{\"name\": 123}", "user")]
    [InlineData("POST", "/api/folders", "{\"name\": [\"a\"]}", "user")]
    [InlineData("PUT", "/api/folders/{id}", "{\"name\": {}}", "user")]
    // snippets
    [InlineData("POST", "/api/snippets", "{\"content\": 123}", "user")]
    [InlineData("POST", "/api/snippets", "{\"content\": \"x\", \"expiresAt\": \"soon\"}", "user")]
    [InlineData("POST", "/api/snippets", "{\"content\": \"x\", \"renderMode\": [\"code\"]}", "user")]
    [InlineData("POST", "/api/snippets", "{\"content\": \"x\", \"public\": \"true\"}", "user")]
    [InlineData("POST", "/api/snippets", "{\"content\": \"x\", \"title\": 9}", "user")]
    [InlineData("PUT", "/api/snippets/{id}", "{\"content\": \"x\", \"baseVersion\": 99999999999999}", "user")]
    [InlineData("PUT", "/api/snippets/{id}", "{\"content\": 7, \"baseVersion\": 1}", "user")]
    // resources / uploads / content
    // PATCH /api/resources/{id} resolves ownership before it parses its JsonElement body (ParsePatch checks every property kind), so a random id is a 404.
    [InlineData("POST", "/api/uploads/{id}/complete", "{\"title\": 5}", "user")]
    [InlineData("POST", "/api/uploads/{id}/complete", "{\"burnAfterDownload\": \"x\"}", "user")]
    [InlineData("POST", "/api/public/content/abc123/unlock", "{\"password\": 123}", "anon")]
    [InlineData("POST", "/api/unlock/abc123", "{\"password\": [1]}", "anon")]
    // auth
    [InlineData("POST", "/api/auth/register", "{\"email\": 1, \"password\": \"x\"}", "anon")]
    [InlineData("POST", "/api/auth/register", "{\"email\": \"a@b.co\", \"password\": 12345678, \"displayName\": []}", "anon")]
    [InlineData("POST", "/api/auth/login", "{\"email\": [\"a\"], \"password\": \"x\"}", "anon")]
    [InlineData("POST", "/api/auth/login", "{\"email\": \"a@b.co\", \"password\": \"x\", \"totpCode\": 123456}", "anon")]
    [InlineData("POST", "/api/auth/refresh", "{\"refreshToken\": 5}", "anon")]
    [InlineData("POST", "/api/auth/logout", "{\"refreshToken\": {}}", "anon")]
    [InlineData("POST", "/api/auth/confirm-email", "{\"userId\": 5, \"token\": \"x\"}", "anon")]
    [InlineData("POST", "/api/auth/confirm-email", "{\"userId\": \"nope\", \"token\": \"x\"}", "anon")]
    [InlineData("POST", "/api/auth/forgot-password", "{\"email\": 5}", "anon")]
    [InlineData("POST", "/api/auth/reset-password", "{\"userId\": \"x\"}", "anon")]
    [InlineData("POST", "/api/auth/resend-confirmation", "{\"email\": 5}", "anon")]
    [InlineData("POST", "/api/auth/totp/confirm", "{\"code\": 123456}", "user")]
    // me / keys / device
    [InlineData("DELETE", "/api/me", "{\"password\": 5}", "user")]
    [InlineData("POST", "/api/api-keys", "{\"name\": 1, \"scopes\": \"links:read\"}", "user")]
    [InlineData("POST", "/api/api-keys", "{\"name\": \"k\", \"scopes\": [1, 2]}", "user")]
    [InlineData("POST", "/api/auth/device/activate", "{\"userCode\": 5, \"approve\": \"yes\"}", "user")]
    // reports
    [InlineData("POST", "/api/reports", "{\"category\": 5}", "anon")]
    [InlineData("POST", "/api/reports", "{\"category\": \"spam\", \"details\": {}}", "anon")]
    // billing has no request body of its own; a body of the wrong type must not matter, so it is covered by the malformed-JSON case below.
    public async Task Every_body_taking_endpoint_answers_a_wrongly_typed_body_with_a_clean_problem(string method, string path, string json, string who)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "sweep");
        using var client = who == "user" ? api.ApiClient(bearer: owner.AccessToken) : api.ApiClient();
        var url = path.Replace("{id}", Guid.NewGuid().ToString());
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = Body(json) };
        request.Headers.Add("Idempotency-Key", "k-" + TestIps.UniqueSuffix());
        using var r = await client.SendAsync(request);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode != HttpStatusCode.InternalServerError && (int)r.StatusCode < 500, $"{method} {path} {json} -> {(int)r.StatusCode}: {text}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await AssertCleanProblem(r, $"{method} {path} {json}", "invalid_request", "validation");
    }

    [Theory]
    [InlineData("/api/billing/checkout")]
    [InlineData("/api/billing/portal")]
    public async Task Billing_endpoints_ignore_a_malformed_body_instead_of_failing(string path)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "billing");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsync(path, Body("{\"x\": [1, \"a\", {}"));
        Assert.True((int)r.StatusCode < 500 || r.StatusCode == HttpStatusCode.ServiceUnavailable, $"{path} -> {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    // ---- 3. Numeric upper bounds -------------------------------------------------------------------------------------------

    [Fact]
    public async Task MaxClicks_above_the_cap_is_a_field_level_400()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "caps-clicks");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsJsonAsync("/api/links", new LinkInput(Good, Policy: new PolicyInput(MaxClicks: 100_000_001)), Json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Click limit", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_14_digit_maxClicks_on_create_and_update_is_a_400_that_names_the_field()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "caps-14");
        const string tooBig = "{\"destinationUrl\": \"https://example.com/caps\", \"policy\": {\"maxClicks\": 99999999999999}}";
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var create = await client.PostAsync("/api/links", Body(tooBig));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Contains("maxClicks", await create.Content.ReadAsStringAsync());

        var link = await api.Principals.CreateLinkAsync(owner);
        using var update = await client.PutAsync($"/api/links/{link.Id}", Body(tooBig));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Contains("maxClicks", await update.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("2100-01-02T00:00:00Z")]
    [InlineData("3000-01-01T00:00:00Z")]
    public async Task An_expiry_date_far_in_the_future_is_a_400_not_a_date_overflow(string expiresAt)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "caps-expiry");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsync("/api/links", Body($"{{\"destinationUrl\": \"https://example.com/exp\", \"policy\": {{\"expiresAt\": \"{expiresAt}\"}}}}"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await AssertCleanProblem(r, expiresAt, "validation");

        var link = await api.Principals.CreateLinkAsync(owner);
        using var renew = await client.PostAsync($"/api/links/{link.Id}/renew", Body($"{{\"expiresAt\": \"{expiresAt}\"}}"));
        Assert.True((int)renew.StatusCode is >= 400 and < 500, $"renew -> {(int)renew.StatusCode}: {await renew.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Oversized_lengths_are_field_level_400s()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "caps-len");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        foreach (var input in new[]
                 {
                     new LinkInput(Good, Title: new string('t', 201)),
                     new LinkInput(Good, Description: new string('d', 1001)),
                     new LinkInput("https://example.com/" + new string('a', 2100)),
                     new LinkInput(Good, Tags: Enumerable.Range(0, 11).Select(i => "t" + i).ToArray()),
                 })
        {
            using var r = await client.PostAsJsonAsync("/api/links", input, Json);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            await AssertCleanProblem(r, "oversized", "validation");
        }
    }

    [Theory]
    [InlineData("/api/links?pageSize=1000000")]
    [InlineData("/api/links?page=2147483647&pageSize=2147483647")]
    public async Task Huge_page_parameters_do_not_fail(string url)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "caps-page");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.GetAsync(url);
        Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"{url} -> {(int)r.StatusCode}");
    }

    // ---- 4. SSRF-style destinations stay refused over HTTP -----------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://127.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:8080/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("http://admin@example.com/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[::127.0.0.1]/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2002:7f00:1::]/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://intranet.internal/")]
    [InlineData("ftp://example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public async Task Internal_and_unsafe_destinations_are_a_400(string destination)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "ssrf");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsJsonAsync("/api/links", new LinkInput(destination), Json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await AssertCleanProblem(r, destination, "validation");
    }

    [Fact]
    public async Task The_first_loopback_request_after_startup_is_a_validation_error_not_a_server_error()
    {
        // item 4: the loopback check is purely syntactic (no DNS, no network); a brand-new host answers it on the very first call.
        var owner = await api.Principals.CreateUserAsync(tag: "first");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var r = await client.PostAsJsonAsync("/api/links", new LinkInput("http://127.0.0.1/"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Private and loopback", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Internal_fallback_and_rule_urls_are_refused_too()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "ssrf2");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var fallback = await client.PostAsJsonAsync("/api/links", new LinkInput(Good, Policy: new PolicyInput(FallbackUrl: "http://169.254.169.254/")), Json);
        Assert.Equal(HttpStatusCode.BadRequest, fallback.StatusCode);
        using var rule = await client.PostAsJsonAsync("/api/links",
            new LinkInput(Good, Rules: [new RuleInput("device", "ios", "http://[::1]/"), new RuleInput("device", "android", Good)]), Json);
        Assert.Equal(HttpStatusCode.BadRequest, rule.StatusCode);
    }

    // ---- ApiExceptionHandler keeps real faults as 500 -------------------------------------------------------------------

    [Fact]
    public async Task Guest_create_with_a_wrong_type_is_also_a_400()
    {
        using var anon = api.ApiClient();
        using var r = await anon.PostAsync("/api/links", Body("{\"destinationUrl\": 12345}"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await AssertCleanProblem(r, "guest", "invalid_request");
    }
}
