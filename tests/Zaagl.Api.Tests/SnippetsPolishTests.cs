using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.Modules.Snippets.Infrastructure;

namespace Zaagl.Api.Tests;

/// <summary>Small guest and diff limits so the tests stay fast; guest per-minute brake lifted.</summary>
public sealed class SnippetsPolishFactory : SnippetsApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings().Concat(
        [
            new KeyValuePair<string, string>("Guests:PerMinute", "1000"),
            new KeyValuePair<string, string>("Guests:ContentPerHour", "3"),
            new KeyValuePair<string, string>("Guests:ContentPerDay", "5"),
            new KeyValuePair<string, string>("Snippets:MaxDiffLines", "50"),
        ]);
}

/// <summary>BE-009 diff, BE-010 raw ingest, BE-026 guest counters, owner delete (ARC-010 §4.4, §10; PD-14, PD-55).</summary>
[Trait("Area", "BE-009")]
public sealed class SnippetsPolishTests(SnippetsPolishFactory api) : IClassFixture<SnippetsPolishFactory>
{
    private static readonly JsonSerializerOptions Json = TestPrincipals.Json;
    private TestPrincipals P => api.Principals;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(JsonElement Body, HttpStatusCode Status)> CreateAsync(HttpClient client, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/snippets") { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Idempotency-Key", "k-" + TestIps.UniqueSuffix());
        using var response = await client.SendAsync(request);
        return (await BodyAsync(response), response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string content, int baseVersion) =>
        await client.PutAsJsonAsync($"/api/snippets/{id}", new { content, baseVersion }, Json);

    private static Guid Id(JsonElement created) => created.GetProperty("resourceId").GetGuid();

    private static HttpRequestMessage Raw(string query, byte[] body, string contentType, string? accept = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/snippets/raw" + query) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    // ---- diff -----------------------------------------------------------------------------------

    [Fact]
    public async Task Diff_pairs_changed_lines_and_is_cacheable_owner_only_and_validated()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "a\nb\nc\n", renderMode = "markdown" });
        var id = Id(created);
        using (var put = await PutAsync(client, id, "a\nB\nc\nd\n", 1)) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var diff = await client.GetAsync($"/api/snippets/{id}/diff?from=1&to=2");
        Assert.Equal(HttpStatusCode.OK, diff.StatusCode);
        Assert.Equal("\"d-1-2-0\"", diff.Headers.ETag!.Tag);
        Assert.True(diff.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromSeconds(3600), diff.Headers.CacheControl.MaxAge);
        var raw = await diff.Content.ReadAsStringAsync();
        Assert.DoesNotContain("renderedHtml", raw, StringComparison.OrdinalIgnoreCase); // PD-09 option A: text only
        var body = JsonDocument.Parse(raw).RootElement;
        var stats = body.GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("added").GetInt32());
        Assert.Equal(0, stats.GetProperty("removed").GetInt32());
        Assert.Equal(1, stats.GetProperty("changed").GetInt32());
        var rows = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("unchanged", rows[0].GetProperty("left").GetProperty("type").GetString());
        Assert.Equal("modified", rows[1].GetProperty("left").GetProperty("type").GetString());
        Assert.Equal("B", rows[1].GetProperty("right").GetProperty("text").GetString());
        Assert.Equal("imaginary", rows[3].GetProperty("left").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, rows[3].GetProperty("left").GetProperty("no").ValueKind);
        Assert.Equal(4, rows[3].GetProperty("right").GetProperty("no").GetInt32());

        // Either order; the reverse turns the insert into a delete.
        var reverse = await BodyAsync(await client.GetAsync($"/api/snippets/{id}/diff?from=2&to=1"));
        Assert.Equal(1, reverse.GetProperty("stats").GetProperty("removed").GetInt32());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/snippets/{id}/diff?from=1&to=2");
        conditional.Headers.IfNoneMatch.ParseAdd("\"d-1-2-0\"");
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/snippets/{id}/diff?from=1&to=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/snippets/{id}/diff?from=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/snippets/{id}/diff?from=1&to=9")).StatusCode);

        var other = await P.CreateUserAsync(starter: false);
        using var stranger = api.ApiClient(bearer: other.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/snippets/{id}/diff?from=1&to=2")).StatusCode);
        using var anonymous = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/snippets/{id}/diff?from=1&to=2")).StatusCode);
    }

    [Fact]
    public async Task Diff_can_ignore_whitespace()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "x = 1\ny = 2\n" });
        using (await PutAsync(client, Id(created), "  x = 1  \ny = 2\n", 1)) { }

        var strict = await BodyAsync(await client.GetAsync($"/api/snippets/{Id(created)}/diff?from=1&to=2"));
        Assert.Equal(1, strict.GetProperty("stats").GetProperty("changed").GetInt32());
        var lenient = await BodyAsync(await client.GetAsync($"/api/snippets/{Id(created)}/diff?from=1&to=2&ignoreWhitespace=true"));
        Assert.Equal(0, lenient.GetProperty("stats").GetProperty("changed").GetInt32());
    }

    [Fact]
    public async Task Diff_of_too_many_lines_is_413_diff_too_large()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var forty = string.Join('\n', Enumerable.Range(0, 40).Select(i => "line " + i));
        var (created, _) = await CreateAsync(client, new { content = forty });
        using (await PutAsync(client, Id(created), forty + "\nmore", 1)) { }

        using var diff = await client.GetAsync($"/api/snippets/{Id(created)}/diff?from=1&to=2"); // 40 + 41 > 50
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, diff.StatusCode);
        Assert.Equal("diff-too-large", (await BodyAsync(diff)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Markdown_is_stored_and_returned_as_text_only()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, status) = await CreateAsync(client, new { content = "# Title\n<script>alert(1)</script>\n", renderMode = "markdown" });
        Assert.Equal(HttpStatusCode.Created, status);
        using var view = await client.GetAsync($"/api/snippets/{Id(created)}/versions/1");
        var json = await BodyAsync(view);
        Assert.Equal("markdown", json.GetProperty("renderMode").GetString());
        Assert.Equal("# Title\n<script>alert(1)</script>\n", json.GetProperty("content").GetString()); // inert text, untouched
        Assert.False(json.TryGetProperty("renderedHtml", out _));
    }

    // ---- raw ingest -----------------------------------------------------------------------------

    [Fact]
    public async Task Raw_creates_from_utf8_and_answers_one_line_for_text_plain()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var text = "echo héllo \U0001F600\r\n  trailing  \n";
        using var response = await client.SendAsync(Raw("?lang=Bash&title=Hi", Encoding.UTF8.GetBytes(text), "text/plain", accept: "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        var line = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("\n", line);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
        Assert.Matches("#k=[A-Za-z0-9_-]{22}\n$", line);

        // The stored text is byte for byte what was sent; the language is normalised.
        var code = line[(line.LastIndexOf('/') + 1)..line.IndexOf('#')];
        using var scope = api.Services.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<LinksDbContext>().Links.AsNoTracking();
        var resourceId = (await links.SingleAsync(l => l.SlugKey == code.ToLowerInvariant())).ResourceId!.Value;
        var stored = await scope.ServiceProvider.GetRequiredService<SnippetsDbContext>().Versions.AsNoTracking().SingleAsync(v => v.ResourceId == resourceId);
        Assert.Equal(text, stored.Content);
        Assert.Equal("bash", stored.Language);
    }

    [Fact]
    public async Task Raw_without_a_text_plain_accept_returns_the_json_created_body()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var response = await client.SendAsync(Raw("", "plain text"u8.ToArray(), "text/plain; charset=utf-8"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.Equal(22, body.GetProperty("linkKey").GetString()!.Length);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public async Task Raw_accepts_utf16_by_charset_and_stores_utf8_text(string charset)
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var text = "سلام world \U0001F600\r\nline";
        var encoding = new UnicodeEncoding(bigEndian: charset == "utf-16be", byteOrderMark: false);
        using var response = await client.SendAsync(Raw("", encoding.GetBytes(text), $"text/plain; charset={charset}"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyAsync(response);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), created.GetProperty("sizeBytes").GetInt32());
        using var read = await client.GetAsync($"/api/snippets/{Id(created)}/versions/1");
        Assert.Equal(text, (await BodyAsync(read)).GetProperty("content").GetString());
    }

    [Fact]
    public async Task Raw_detects_a_utf16_bom_even_without_a_charset_and_consumes_it()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("from PowerShell")).ToArray();
        using var response = await client.SendAsync(Raw("", bytes, "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyAsync(response);
        Assert.Equal("from PowerShell".Length, created.GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public async Task Raw_refuses_wrong_type_charset_binary_empty_and_guests()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);

        using var json = await client.SendAsync(Raw("", "x"u8.ToArray(), "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        using var latin = await client.SendAsync(Raw("", "x"u8.ToArray(), "text/plain; charset=iso-8859-1"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, latin.StatusCode);
        using var invalid = await client.SendAsync(Raw("", [0x61, 0xC3, 0x28, 0x62], "text/plain; charset=utf-8"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, invalid.StatusCode);
        using var nul = await client.SendAsync(Raw("", "a\0b"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nul.StatusCode);
        using var odd = await client.SendAsync(Raw("", [0x61, 0x00, 0x62], "text/plain; charset=utf-16"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, odd.StatusCode);
        using var empty = await client.SendAsync(Raw("", [], "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var badMode = await client.SendAsync(Raw("?renderMode=html", "x"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);

        using var guest = api.ApiClient();
        using var anonymous = await guest.SendAsync(Raw("", "x"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var badToken = api.ApiClient(bearer: "not-a-real-token");
        using var failing = await badToken.SendAsync(Raw("", "x"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.Unauthorized, failing.StatusCode);
    }

    [Fact]
    public async Task Raw_enforces_the_plan_size_cap_in_utf8_bytes()
    {
        var owner = await P.CreateUserAsync(starter: false); // Free: 524,288
        using var client = api.ApiClient(bearer: owner.AccessToken);

        using var atCap = await client.SendAsync(Raw("", Enumerable.Repeat((byte)'a', 524_288).ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.Created, atCap.StatusCode);
        using var over = await client.SendAsync(Raw("", Enumerable.Repeat((byte)'a', 524_289).ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal("snippet-too-large", (await BodyAsync(over)).GetProperty("code").GetString());
        // Far above the read limit: refused on the declared length, never read.
        using var huge = await client.SendAsync(Raw("", new byte[1_200_000], "text/plain"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        // UTF-16 whose UTF-8 form is over the cap (300,000 x 2 bytes = 600,000 > 524,288).
        using var wide = await client.SendAsync(Raw("", Encoding.Unicode.GetBytes(new string('é', 300_000)), "text/plain; charset=utf-16le"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, wide.StatusCode);
    }

    [Fact]
    public async Task Raw_strips_ansi_for_terminal_unless_keep_and_ignores_it_otherwise()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        const string ansi = "\u001b[31mred\u001b[0m \u001b]0;title\a ok\tx\n";

        async Task<string> StoredAsync(string query)
        {
            using var response = await client.SendAsync(Raw(query, Encoding.UTF8.GetBytes(ansi), "text/plain"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = Id(await BodyAsync(response));
            return (await BodyAsync(await client.GetAsync($"/api/snippets/{id}/versions/1"))).GetProperty("content").GetString()!;
        }

        Assert.Equal("red  ok\tx\n", await StoredAsync("?renderMode=terminal"));
        Assert.Equal(ansi, await StoredAsync("?renderMode=terminal&ansi=keep"));
        Assert.Equal(ansi, await StoredAsync("?renderMode=code"));
    }

    [Fact]
    public async Task Raw_idempotency_key_replays_and_a_changed_body_is_422()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var key = "raw-" + TestIps.UniqueSuffix();
        using var first = await client.SendAsync(Raw("", "same"u8.ToArray(), "text/plain", key: key));
        using var replay = await client.SendAsync(Raw("", "same"u8.ToArray(), "text/plain", key: key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(Id(await BodyAsync(first)), Id(await BodyAsync(replay)));
        using var changed = await client.SendAsync(Raw("", "different"u8.ToArray(), "text/plain", key: key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
    }

    [Fact]
    public async Task Raw_counts_toward_the_owner_quota_like_create()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        using var response = await client.SendAsync(Raw("", "twelve bytes"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = api.Services.CreateScope();
        var ledger = await scope.ServiceProvider.GetRequiredService<ResourcesDbContext>().QuotaLedger.AsNoTracking().SingleAsync(l => l.OwnerId == owner.Id);
        Assert.Equal(12, ledger.UsedBytes);
    }

    // ---- guest counters (PD-14, BE-026) ---------------------------------------------------------

    [Fact]
    public async Task Guest_counters_stop_the_fourth_snippet_in_an_hour_and_do_not_count_failures()
    {
        using var guest = api.ApiClient(); // Guests:ContentPerHour = 3 in this factory
        // A refused snippet (too large for a guest) must not use a slot.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await CreateAsync(guest, new { content = new string('a', 262_145) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(guest, new { content = "" })).Status);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await CreateAsync(guest, new { content = "guest " + i })).Status);

        var (blocked, status) = await CreateAsync(guest, new { content = "one too many" });
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("quota", blocked.GetProperty("code").GetString());

        // Another partition is unaffected, and a signed-in user is never subject to the guest counters.
        using var other = api.ApiClient();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(other, new { content = "elsewhere" })).Status);
        var user = await P.CreateUserAsync(starter: false);
        using var signedIn = api.ApiClient(bearer: user.AccessToken);
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created, (await CreateAsync(signedIn, new { content = "mine " + i })).Status);
    }

    [Fact]
    public async Task Guest_daily_counter_spans_hours()
    {
        using var guest = api.ApiClient(TestIps.RandomV4()); // 3 per hour, 5 per day
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await CreateAsync(guest, new { content = "a" + i })).Status);
        api.Clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Created, (await CreateAsync(guest, new { content = "b" + i })).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CreateAsync(guest, new { content = "c" })).Status);
        api.Clock.Advance(TimeSpan.FromHours(24)); // the first buckets have left the 24 h window
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(guest, new { content = "d" })).Status);
    }

    // ---- delete ---------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_makes_the_link_410_keeps_quota_and_is_idempotent_and_owner_only()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "to delete" });
        var id = Id(created);
        var code = created.GetProperty("code").GetString();
        var key = created.GetProperty("linkKey").GetString()!;

        var other = await P.CreateUserAsync(starter: false);
        using var stranger = api.ApiClient(bearer: other.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/snippets/{id}")).StatusCode);
        using var anonymous = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/snippets/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/snippets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/snippets/{id}")).StatusCode); // also when already deleted

        using var visitor = api.ApiClient();
        visitor.DefaultRequestHeaders.Add("X-Zaagl-Link-Key", key);
        Assert.Equal(HttpStatusCode.Gone, (await visitor.GetAsync($"/api/public/content/{code}")).StatusCode);

        using var detail = await client.GetAsync($"/api/snippets/{id}");
        Assert.Equal("deleted", (await BodyAsync(detail)).GetProperty("state").GetString());
        using var edit = await PutAsync(client, id, "new", 1);
        Assert.Equal(HttpStatusCode.Gone, edit.StatusCode);
        using var diff = await client.GetAsync($"/api/snippets/{id}/diff?from=1&to=2");
        Assert.Equal(HttpStatusCode.Gone, diff.StatusCode);

        // PD-55: the quota is freed only at purge; the link is soft-deleted and a C10 outbox row was written.
        using var scope = api.Services.CreateScope();
        var ledger = await scope.ServiceProvider.GetRequiredService<ResourcesDbContext>().QuotaLedger.AsNoTracking().SingleAsync(l => l.OwnerId == owner.Id);
        Assert.Equal(9, ledger.UsedBytes);
        var links = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
        var link = await links.Links.AsNoTracking().SingleAsync(l => l.ResourceId == id);
        Assert.NotNull(link.DeletedAt);
        Assert.Equal(1, await links.LifecycleOutbox.AsNoTracking()
            .CountAsync(o => o.ResourceId == id && o.Event == Zaagl.Modules.Links.Domain.LifecycleEventType.Deleted));
    }

    [Fact]
    public async Task Delete_needs_the_snippets_write_scope()
    {
        var owner = await P.CreateUserAsync(starter: true);
        var readKey = await P.CreateApiKeyAsync(owner, "resources:read");
        var writeKey = await P.CreateApiKeyAsync(owner, "snippets:write", "resources:read");
        using var session = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(session, new { content = "scoped" });

        using var reader = api.ApiClient(apiKey: readKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/snippets/{Id(created)}")).StatusCode);
        using var writer = api.ApiClient(apiKey: writeKey);
        Assert.Equal(HttpStatusCode.NoContent, (await writer.DeleteAsync($"/api/snippets/{Id(created)}")).StatusCode);
    }
}
