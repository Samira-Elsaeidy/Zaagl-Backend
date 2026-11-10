using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.Modules.Snippets.Infrastructure;

namespace Zaagl.Api.Tests;

/// <summary>The API with a user-content host and the ticket keys set, and no write throttling (the 100-version test).</summary>
public class SnippetsApiFactory : ZaaglApiFactory
{
    public const string UserContentHost = "usercontent.zaa.test";

    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
    [
        new("Hosts:UserContent", UserContentHost),
        new("Content:UserContentBaseUrl", $"http://{UserContentHost}"),
        new("Content:TicketKeys:0:Kid", "t1"),
        new("Content:TicketKeys:0:Key", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("snippets-tests-ticket-key")))),
        new("Hosts:RewriteDeliveryPath", "true"),
        new("RateLimits:ContentWritePerMinute", "100000"),
        new("RateLimits:ContentReadPerMinute", "100000"),
    ];
}

/// <summary>
/// BE-008 (ARC-003, ARC-010, contracts.md C9.1, F-4 to F-6): snippet create, immutable versions, public read and raw
/// delivery over HTTP on the real Program and the real database.
/// </summary>
[Trait("Area", "BE-008")]
public sealed class SnippetsApiTests(SnippetsApiFactory api) : IClassFixture<SnippetsApiFactory>
{
    private static readonly JsonSerializerOptions Json = TestPrincipals.Json;
    private TestPrincipals P => api.Principals;

    private static string Key() => "k-" + TestIps.UniqueSuffix();

    private static HttpRequestMessage Post(string url, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.True(response.Headers.CacheControl.Private);
    }

    private async Task<(JsonElement Body, HttpStatusCode Status)> CreateAsync(HttpClient client, object body, string? key = null)
    {
        using var response = await client.SendAsync(Post("/api/snippets", body, key ?? Key()));
        return (await BodyAsync(response), response.StatusCode);
    }

    private static string Code(JsonElement created) => created.GetProperty("code").GetString()!;
    private static Guid Id(JsonElement created) => created.GetProperty("resourceId").GetGuid();
    private static string LinkKey(JsonElement created) => created.GetProperty("linkKey").GetString()!;

    private HttpClient Public(string? linkKey = null, string? grant = null)
    {
        var client = api.ApiClient();
        if (linkKey is not null) client.DefaultRequestHeaders.Add("X-Zaagl-Link-Key", linkKey);
        if (grant is not null) client.DefaultRequestHeaders.Add("X-Zaagl-Grant", grant);
        return client;
    }

    private static string TicketOf(string rawUrl) => rawUrl[(rawUrl.IndexOf("/d/", StringComparison.Ordinal) + 3)..];

    /// <summary>The Links admin tests leave an active alias freeze in the shared database, which refuses every new
    /// custom alias; tests that create aliases isolate themselves the same way those tests do.</summary>
    private async Task ClearAliasFreezesAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LinksDbContext>().AliasFreezes.ExecuteDeleteAsync();
    }

    // ---- create ---------------------------------------------------------------------------------

    [Fact]
    public async Task Create_stores_text_byte_for_byte_and_returns_the_link_key_once()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var text = "﻿cafe é \U0001F600\r\nline two  \r\n\ttab\n";

        var (created, status) = await CreateAsync(client, new { content = text, language = "C#", title = "  Hello‮  " });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(22, LinkKey(created).Length);
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), created.GetProperty("sizeBytes").GetInt32());
        Assert.Equal(3, created.GetProperty("lineCount").GetInt32()); // 3 newlines, ends with \n
        Assert.False(created.GetProperty("isGuest").GetBoolean());
        Assert.DoesNotContain('#', created.GetProperty("shortUrl").GetString()!);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SnippetsDbContext>();
        var row = await db.Versions.AsNoTracking().SingleAsync(v => v.ResourceId == Id(created));
        Assert.Equal(text, row.Content);
        Assert.Equal("c#", row.Language);
        Assert.Equal(1, (await db.Snippets.AsNoTracking().SingleAsync(s => s.ResourceId == Id(created))).CurrentVersion);
    }

    [Fact]
    public async Task Create_needs_an_idempotency_key_and_validates_the_text()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);

        using var noKey = await client.SendAsync(Post("/api/snippets", new { content = "x" }, null));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, new { content = "" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, new { content = "a\u0000b" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, new { content = "x", language = "not a language" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, new { content = "x", renderMode = "html" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, new { content = "x", title = new string('t', 201) })).Status);
        // A lone surrogate is not valid UTF-8.
        using var lone = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/snippets")
        {
            Content = new StringContent("{\"content\":\"a\\ud800b\"}", Encoding.UTF8, "application/json"),
            Headers = { { "Idempotency-Key", Key() } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, lone.StatusCode);
    }

    [Fact]
    public async Task Idempotent_replay_returns_the_same_snippet_without_the_key_and_a_changed_body_is_422()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var key = Key();

        var (first, firstStatus) = await CreateAsync(client, new { content = "same" }, key);
        var (second, secondStatus) = await CreateAsync(client, new { content = "same" }, key);
        var (other, otherStatus) = await CreateAsync(client, new { content = "different" }, key);

        Assert.Equal(HttpStatusCode.Created, firstStatus);
        Assert.Equal(HttpStatusCode.Created, secondStatus);
        Assert.Equal(Id(first), Id(second));
        Assert.Equal(Code(first), Code(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("linkKey").ValueKind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherStatus);
        Assert.Equal("idempotency-mismatch", other.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Plan_rules_size_cap_password_and_expiry()
    {
        var free = await P.CreateUserAsync(starter: false);
        var starter = await P.CreateUserAsync(starter: true);
        using var freeClient = api.ApiClient(bearer: free.AccessToken);
        using var starterClient = api.ApiClient(bearer: starter.AccessToken);

        var (tooBig, tooBigStatus) = await CreateAsync(freeClient, new { content = new string('a', 524_289) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBigStatus);
        Assert.Equal("snippet-too-large", tooBig.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(freeClient, new { content = new string('a', 524_288) })).Status);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(starterClient, new { content = new string('a', 600_000) })).Status);

        var (needsPlan, planStatus) = await CreateAsync(freeClient, new { content = "x", password = "secret-pass" });
        Assert.Equal(HttpStatusCode.PaymentRequired, planStatus);
        Assert.Equal("plan_required", needsPlan.GetProperty("code").GetString());

        var (beyond, beyondStatus) = await CreateAsync(freeClient, new { content = "x", expiresAt = DateTime.UtcNow.AddDays(30) });
        Assert.Equal(HttpStatusCode.BadRequest, beyondStatus);
        Assert.Equal("expiry-beyond-plan", beyond.GetProperty("code").GetString());

        var (shorter, shorterStatus) = await CreateAsync(freeClient, new { content = "x", expiresAt = DateTime.UtcNow.AddDays(2) });
        Assert.Equal(HttpStatusCode.Created, shorterStatus);
        Assert.InRange((shorter.GetProperty("expiresAt").GetDateTime() - DateTime.UtcNow).TotalDays, 1.9, 2.1);
    }

    [Fact]
    public async Task Alias_conflict_is_409_and_leaves_no_reserved_bytes()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        await ClearAliasFreezesAsync();
        var alias = "snip-" + TestIps.UniqueSuffix();

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, new { content = "first", customSlug = alias })).Status);
        var (_, status) = await CreateAsync(client, new { content = "second text", customSlug = alias });
        Assert.Equal(HttpStatusCode.Conflict, status);

        using var scope = api.Services.CreateScope();
        var ledger = await scope.ServiceProvider.GetRequiredService<ResourcesDbContext>().QuotaLedger.AsNoTracking().SingleAsync(l => l.OwnerId == owner.Id);
        Assert.Equal(0, ledger.ReservedBytes);
        Assert.Equal(5, ledger.UsedBytes); // only "first"
    }

    // ---- guests ---------------------------------------------------------------------------------

    [Fact]
    public async Task Guests_create_keyed_snippets_only_and_a_failed_credential_is_401()
    {
        using var guest = api.ApiClient();
        var (created, status) = await CreateAsync(guest, new { content = "guest text" });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.True(created.GetProperty("isGuest").GetBoolean());
        Assert.Equal(22, LinkKey(created).Length);

        var (_, publicStatus) = await CreateAsync(guest, new { content = "x", @public = true });
        Assert.Equal(HttpStatusCode.BadRequest, publicStatus); // addendum 5.1.3 to confirm: fail closed

        var (big, bigStatus) = await CreateAsync(guest, new { content = new string('a', 262_145) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigStatus);
        Assert.Equal("snippet-too-large", big.GetProperty("code").GetString());

        await ClearAliasFreezesAsync();
        var (_, aliasStatus) = await CreateAsync(guest, new { content = "x", customSlug = "guest-" + TestIps.UniqueSuffix() });
        Assert.Equal(HttpStatusCode.Created, aliasStatus); // PD-57

        using var bad = api.ApiClient(bearer: "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateAsync(bad, new { content = "x" })).Status);
    }

    // ---- public read ----------------------------------------------------------------------------

    [Fact]
    public async Task Descriptor_needs_the_key_and_answers_a_wrong_key_like_an_unknown_code()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var text = "line1\r\nline2  \n";
        var (created, _) = await CreateAsync(client, new { content = text, language = "python", renderMode = "markdown", title = "T" });

        using var ok = await Public(LinkKey(created)).GetAsync($"/api/public/content/{Code(created)}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        AssertNoStore(ok);
        Assert.Equal("noindex", ok.Headers.GetValues("X-Robots-Tag").Single());
        var d = await BodyAsync(ok);
        Assert.Equal("snippet", d.GetProperty("kind").GetString());
        Assert.Equal("ready", d.GetProperty("state").GetString());
        Assert.Equal("T", d.GetProperty("title").GetString());
        Assert.False(d.GetProperty("burnAfterDownload").GetBoolean());
        Assert.False(d.GetProperty("hasPassword").GetBoolean());
        var snippet = d.GetProperty("snippet");
        Assert.Equal(text, snippet.GetProperty("content").GetString());
        Assert.Equal("python", snippet.GetProperty("language").GetString());
        Assert.Equal("markdown", snippet.GetProperty("renderMode").GetString());
        Assert.Equal([1], snippet.GetProperty("versions").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        Assert.False(snippet.TryGetProperty("renderedHtml", out _));
        Assert.StartsWith($"http://{SnippetsApiFactory.UserContentHost}/d/", d.GetProperty("rawUrl").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("inlineUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("previewUrl").ValueKind);

        // Missing key, wrong key and unknown code: the same status, code and headers.
        using var missing = await Public().GetAsync($"/api/public/content/{Code(created)}");
        using var wrong = await Public(new string('A', 22)).GetAsync($"/api/public/content/{Code(created)}");
        using var unknown = await Public().GetAsync($"/api/public/content/zz{TestIps.UniqueSuffix()}");
        foreach (var r in new[] { missing, wrong, unknown })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("not_found", (await BodyAsync(r)).GetProperty("code").GetString());
            AssertNoStore(r);
            Assert.Equal("noindex", r.Headers.GetValues("X-Robots-Tag").Single());
        }
    }

    [Fact]
    public async Task A_public_snippet_is_readable_without_a_key()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, status) = await CreateAsync(client, new { content = "open", @public = true });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("linkKey").ValueKind);

        using var response = await Public().GetAsync($"/api/public/content/{Code(created)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("open", (await BodyAsync(response)).GetProperty("snippet").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Password_snippets_need_an_unlock_grant()
    {
        var owner = await P.CreateUserAsync(starter: true);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, status) = await CreateAsync(client, new { content = "secret text", password = "open-sesame" });
        Assert.Equal(HttpStatusCode.Created, status);
        var url = $"/api/public/content/{Code(created)}";

        using var locked = await Public(LinkKey(created)).GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("password-required", (await BodyAsync(locked)).GetProperty("code").GetString());
        Assert.False(locked.Headers.Contains("WWW-Authenticate"));

        using var wrong = await Public(LinkKey(created)).PostAsJsonAsync(url + "/unlock", new { password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var noKey = await Public().PostAsJsonAsync(url + "/unlock", new { password = "open-sesame" });
        Assert.Equal(HttpStatusCode.NotFound, noKey.StatusCode);

        using var unlock = await Public(LinkKey(created)).PostAsJsonAsync(url + "/unlock", new { password = "open-sesame" });
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var grant = (await BodyAsync(unlock)).GetProperty("grant").GetString()!;

        using var open = await Public(LinkKey(created), grant).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var d = await BodyAsync(open);
        Assert.True(d.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("secret text", d.GetProperty("snippet").GetProperty("content").GetString());
    }

    // ---- versions -------------------------------------------------------------------------------

    [Fact]
    public async Task Edits_append_immutable_versions_and_visitors_see_the_latest_only()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "v1 text", language = "js" });
        var id = Id(created);

        using var put = await client.SendAsync(Put(id, new { content = "v2 text!", language = "ts", renderMode = "plain", baseVersion = 1 }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var v2 = await BodyAsync(put);
        Assert.Equal(2, v2.GetProperty("version").GetInt32());
        Assert.Equal(8, v2.GetProperty("sizeBytes").GetInt32());
        Assert.Equal("ts", v2.GetProperty("language").GetString());
        Assert.Equal("plain", v2.GetProperty("renderMode").GetString());

        // Stale base version.
        using var stale = await client.SendAsync(Put(id, new { content = "other", baseVersion = 1 }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version-conflict", (await BodyAsync(stale)).GetProperty("code").GetString());

        // A save identical to the current version writes nothing.
        using var noop = await client.SendAsync(Put(id, new { content = "v2 text!", language = "ts", renderMode = "plain", baseVersion = 2 }));
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(2, (await BodyAsync(noop)).GetProperty("version").GetInt32());

        using var list = await client.GetAsync($"/api/snippets/{id}/versions");
        var versions = (await BodyAsync(list)).EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ToArray();
        Assert.Equal([2, 1], versions); // newest first, no third version from the no-op

        using var old = await client.GetAsync($"/api/snippets/{id}/versions/1");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal("v1 text", (await BodyAsync(old)).GetProperty("content").GetString());
        Assert.StartsWith("\"", old.Headers.ETag!.Tag);
        using var missingVersion = await client.GetAsync($"/api/snippets/{id}/versions/9");
        Assert.Equal(HttpStatusCode.NotFound, missingVersion.StatusCode);

        using var publicRead = await Public(LinkKey(created)).GetAsync($"/api/public/content/{Code(created)}");
        var snippet = (await BodyAsync(publicRead)).GetProperty("snippet");
        Assert.Equal("v2 text!", snippet.GetProperty("content").GetString());
        Assert.Equal([2], snippet.GetProperty("versions").EnumerateArray().Select(v => v.GetInt32()).ToArray());

        using var own = await client.GetAsync($"/api/snippets/{id}");
        var detail = await BodyAsync(own);
        Assert.Equal("ready", detail.GetProperty("state").GetString());
        Assert.Equal(2, detail.GetProperty("versionCount").GetInt32());
        Assert.Equal([1, 2], detail.GetProperty("current").GetProperty("versions").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        Assert.False(detail.GetProperty("isPublic").GetBoolean());

        // F-4: the quota counts every stored version (7 + 8 bytes), nothing stays reserved.
        using var scope = api.Services.CreateScope();
        var ledger = await scope.ServiceProvider.GetRequiredService<ResourcesDbContext>().QuotaLedger.AsNoTracking().SingleAsync(l => l.OwnerId == owner.Id);
        Assert.Equal(15, ledger.UsedBytes);
        Assert.Equal(0, ledger.ReservedBytes);
        var reader = scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Snippets.Contracts.ISnippetReader>();
        Assert.Equal(15, await reader.GetTotalBytesAsync(id, default)); // F-6
        var summary = (await reader.GetSummariesAsync([id], default))[id];
        Assert.Equal(2, summary.Version);
        Assert.Equal("ts", summary.Language);
    }

    [Fact]
    public async Task A_retried_edit_with_the_same_idempotency_key_replays_the_stored_answer()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "one" });
        var key = Key();

        using var first = await client.SendAsync(Put(Id(created), new { content = "two", baseVersion = 1 }, key));
        using var replay = await client.SendAsync(Put(Id(created), new { content = "two", baseVersion = 1 }, key));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // not 409 version-conflict
        Assert.Equal(2, (await BodyAsync(replay)).GetProperty("version").GetInt32());
        using var list = await client.GetAsync($"/api/snippets/{Id(created)}/versions");
        Assert.Equal(2, (await BodyAsync(list)).GetArrayLength());
    }

    [Fact]
    public async Task The_101st_version_is_refused_and_the_database_keeps_versions_immutable()
    {
        var owner = await P.CreateUserAsync(starter: true);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "base" });
        var id = Id(created);

        for (var v = 1; v < 100; v++)
        {
            using var put = await client.SendAsync(Put(id, new { content = $"text {v}", baseVersion = v }));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }
        using var over = await client.SendAsync(Put(id, new { content = "one too many", baseVersion = 100 }));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("version-limit", (await BodyAsync(over)).GetProperty("code").GetString());

        // Immutability trigger: the text cannot change, the quota flag can.
        await using var conn = new NpgsqlConnection(TestDb.ConnectionString);
        await conn.OpenAsync();
        await using (var update = new NpgsqlCommand("UPDATE snippets.snippet_versions SET content = 'changed' WHERE resource_id = @id AND version = 1", conn))
        {
            update.Parameters.AddWithValue("id", id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Contains("immutable", ex.MessageText);
        }
        await using (var flag = new NpgsqlCommand("UPDATE snippets.snippet_versions SET quota_committed = true WHERE resource_id = @id AND version = 1", conn))
        {
            flag.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await flag.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task Only_the_owner_can_read_or_edit_and_an_api_key_needs_its_scope()
    {
        var owner = await P.CreateUserAsync(starter: true);
        var other = await P.CreateUserAsync(starter: true);
        using var ownerClient = api.ApiClient(bearer: owner.AccessToken);
        using var otherClient = api.ApiClient(bearer: other.AccessToken);
        var (created, _) = await CreateAsync(ownerClient, new { content = "mine" });
        var id = Id(created);

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/snippets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/snippets/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/snippets/{id}/versions/1")).StatusCode);
        using var steal = await otherClient.SendAsync(Put(id, new { content = "stolen", baseVersion = 1 }));
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ApiClient().GetAsync($"/api/snippets/{id}")).StatusCode);

        var readOnlyKey = await P.CreateApiKeyAsync(owner, "resources:read");
        using var readKey = api.ApiClient(apiKey: readOnlyKey);
        Assert.Equal(HttpStatusCode.OK, (await readKey.GetAsync($"/api/snippets/{id}")).StatusCode);
        using var writeWithRead = await readKey.SendAsync(Put(id, new { content = "x", baseVersion = 1 }));
        Assert.Equal(HttpStatusCode.Forbidden, writeWithRead.StatusCode);
        Assert.Equal("insufficient-scope", (await BodyAsync(writeWithRead)).GetProperty("code").GetString());
        using var createWithRead = await readKey.SendAsync(Post("/api/snippets", new { content = "x" }, Key()));
        Assert.Equal(HttpStatusCode.Forbidden, createWithRead.StatusCode);

        var writeKey = await P.CreateApiKeyAsync(owner, "snippets:write");
        using var writer = api.ApiClient(apiKey: writeKey);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(writer, new { content = "via key" })).Status);
    }

    // ---- raw delivery (H4) ----------------------------------------------------------------------

    [Fact]
    public async Task Raw_delivery_serves_the_stored_bytes_as_an_inert_attachment()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var text = "﻿<script>alert(1)</script>\r\né\t \n";
        var (created, _) = await CreateAsync(client, new { content = text, language = "html" });

        using var descriptor = await Public(LinkKey(created)).GetAsync($"/api/public/content/{Code(created)}");
        var ticket = TicketOf((await BodyAsync(descriptor)).GetProperty("rawUrl").GetString()!);
        var expected = new UTF8Encoding(false).GetBytes(text);

        using var hop = api.HostClient(SnippetsApiFactory.UserContentHost);
        using var get = await hop.GetAsync($"/internal/delivery/{ticket}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(expected, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/plain; charset=utf-8", get.Content.Headers.ContentType!.ToString());
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(expected))}\"", get.Headers.ETag!.Tag);
        Assert.Equal("attachment", get.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"{Code(created)}.txt", get.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        Assert.Equal("bytes", get.Headers.AcceptRanges.Single());
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("sandbox;", get.Headers.GetValues("Content-Security-Policy").Single());
        AssertNoStore(get);
        Assert.False(get.Headers.Contains("Set-Cookie"));
        Assert.False(get.Headers.Contains("Access-Control-Allow-Origin"));

        using var range = new HttpRequestMessage(HttpMethod.Get, $"/internal/delivery/{ticket}");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2);
        using var partial = await hop.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(expected[..3], await partial.Content.ReadAsByteArrayAsync());

        using var head = await hop.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/internal/delivery/{ticket}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        // The authorize hop is reachable only on the user-content host.
        using var onApi = await api.ApiClient().GetAsync($"/internal/delivery/{ticket}");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, onApi.StatusCode);
    }

    [Fact]
    public async Task Raw_tickets_reject_tampering_expiry_and_closed_links()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "raw me" });
        using var descriptor = await Public(LinkKey(created)).GetAsync($"/api/public/content/{Code(created)}");
        var ticket = TicketOf((await BodyAsync(descriptor)).GetProperty("rawUrl").GetString()!);
        using var hop = api.HostClient(SnippetsApiFactory.UserContentHost);

        var tampered = ticket[..^2] + (ticket[^1] == 'A' ? "BB" : "AA");
        foreach (var bad in new[] { tampered, "garbage", ticket + "x", new string('a', 300) })
        {
            using var r = await hop.GetAsync($"/internal/delivery/{bad}");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("ticket-invalid", (await BodyAsync(r)).GetProperty("code").GetString());
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        }

        // A disabled link: the hop reads the link fresh, so the takedown is seen at once (410 gone).
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            var linkId = await scope.ServiceProvider.GetRequiredService<ResourcesDbContext>().Resources.AsNoTracking()
                .Where(r => r.Id == Id(created)).Select(r => r.LinkId).SingleAsync();
            await links.Links.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.DisabledAt, DateTime.UtcNow).SetProperty(l => l.DisabledKind, "abuse"));
        }
        using var gone = await hop.GetAsync($"/internal/delivery/{ticket}");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Equal("gone", (await BodyAsync(gone)).GetProperty("code").GetString());

        // Tickets expire after 15 minutes.
        var (second, _) = await CreateAsync(client, new { content = "second" });
        using var d2 = await Public(LinkKey(second)).GetAsync($"/api/public/content/{Code(second)}");
        var ticket2 = TicketOf((await BodyAsync(d2)).GetProperty("rawUrl").GetString()!);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        try
        {
            using var expired = await hop.GetAsync($"/internal/delivery/{ticket2}");
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        }
        finally
        {
            api.Clock.Advance(TimeSpan.FromMinutes(-16));
        }
    }

    [Fact]
    public async Task In_development_the_ticket_url_path_d_reaches_the_hop_only_on_the_user_content_host()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (created, _) = await CreateAsync(client, new { content = "dev raw" });
        using var descriptor = await Public(LinkKey(created)).GetAsync($"/api/public/content/{Code(created)}");
        var rawUrl = (await BodyAsync(descriptor)).GetProperty("rawUrl").GetString()!;

        using var hop = api.HostClient(SnippetsApiFactory.UserContentHost);
        using var viaD = await hop.GetAsync(new Uri(rawUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, viaD.StatusCode);
        Assert.Equal("dev raw", await viaD.Content.ReadAsStringAsync());

        using var onApi = await api.ApiClient().GetAsync(new Uri(rawUrl).PathAndQuery);
        Assert.NotEqual(HttpStatusCode.OK, onApi.StatusCode);
    }

    [Fact]
    public async Task A_raw_ticket_of_one_snippet_cannot_serve_another()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var (a, _) = await CreateAsync(client, new { content = "AAA" });
        var (b, _) = await CreateAsync(client, new { content = "BBB" });
        using var da = await Public(LinkKey(a)).GetAsync($"/api/public/content/{Code(a)}");
        using var db = await Public(LinkKey(b)).GetAsync($"/api/public/content/{Code(b)}");
        using var hop = api.HostClient(SnippetsApiFactory.UserContentHost);

        using var ra = await hop.GetAsync($"/internal/delivery/{TicketOf((await BodyAsync(da)).GetProperty("rawUrl").GetString()!)}");
        using var rb = await hop.GetAsync($"/internal/delivery/{TicketOf((await BodyAsync(db)).GetProperty("rawUrl").GetString()!)}");
        Assert.Equal("AAA", await ra.Content.ReadAsStringAsync());
        Assert.Equal("BBB", await rb.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Put(Guid id, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/snippets/{id}") { Content = JsonContent.Create(body, options: Json) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }
}
