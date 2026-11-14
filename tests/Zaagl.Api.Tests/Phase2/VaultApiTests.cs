using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.DeveloperTools.Application;
using Zaagl.Modules.DeveloperTools.Infrastructure;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.1 vault (docs/phase2/vault.md): ciphertext-only storage, one-time reveal under concurrency, plan caps,
/// envelope validation, guests, owner routes (IDOR), expiry wipe, password gate, kind isolation.</summary>
[Trait("Area", "P2-Vault")]
public sealed class VaultApiTests(Phase2Factory api) : IClassFixture<Phase2Factory>
{
    private TestPrincipals P => api.Principals;

    private static object Body(string envelope, int? maxViews = null, string? password = null, DateTime? expiresAt = null,
        bool passphrase = false, string? label = null) =>
        new { ciphertext = envelope, maxViews, password, expiresAt, passphraseProtected = passphrase, label };

    private async Task<(System.Text.Json.JsonElement Created, string EncKey, string Envelope)> CreateAsync(
        HttpClient client, string secret = "API_KEY=s3cr3t", int? maxViews = null, string? password = null, DateTime? expiresAt = null)
    {
        var (envelope, encKey) = VaultCrypto.Encrypt($"{{\"v\":1,\"type\":\"env\",\"content\":\"{secret}\"}}");
        var (status, body) = await P2.SendAsync(client, HttpMethod.Post, "/api/vault/secrets", Body(envelope, maxViews, password, expiresAt));
        Assert.True(status == HttpStatusCode.Created, $"create -> {(int)status}: {body}");
        return (body, encKey, envelope);
    }

    [Fact]
    public async Task Create_describe_reveal_once_then_burned_and_the_server_holds_only_the_envelope()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var owner = api.ApiClient(bearer: user.AccessToken);
        var (created, encKey, envelope) = await CreateAsync(owner);
        var code = created.Code();
        var linkKey = created.GetProperty("linkKey").GetString()!;
        Assert.Equal(22, linkKey.Length);
        Assert.Equal(1, created.GetProperty("maxViews").GetInt32());
        Assert.DoesNotContain("#", created.GetProperty("shortUrl").GetString()!);

        using var visitor = api.ApiClient();
        // Without the key, or with a wrong key: the same 404 as an unknown code.
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/vault/{code}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/vault/{code}", headers: P2.Key("AAAAAAAAAAAAAAAAAAAAAA"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(null))).Status);

        // The descriptor consumes nothing, however often it is read.
        for (var i = 0; i < 3; i++)
        {
            using var d = await P2.RawAsync(visitor, HttpMethod.Get, $"/api/public/vault/{code}", headers: P2.Key(linkKey));
            Assert.Equal(HttpStatusCode.OK, d.StatusCode);
            Assert.True(d.Headers.CacheControl!.NoStore);
            var json = System.Text.Json.JsonDocument.Parse(await d.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("available", json.GetProperty("state").GetString());
            Assert.Equal(1, json.GetProperty("viewsRemaining").GetInt32());
            Assert.False(json.TryGetProperty("ciphertext", out _));
        }

        var (revealStatus, reveal) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(linkKey));
        Assert.Equal(HttpStatusCode.OK, revealStatus);
        Assert.Equal(envelope, reveal.GetProperty("ciphertext").GetString());
        Assert.True(reveal.GetProperty("burned").GetBoolean());
        Assert.Equal(0, reveal.GetProperty("viewsRemaining").GetInt32());
        Assert.Contains("s3cr3t", VaultCrypto.Decrypt(reveal.GetProperty("ciphertext").GetString()!, encKey));

        var (again, againBody) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(linkKey));
        Assert.Equal(HttpStatusCode.Gone, again);
        Assert.Equal("burned", againBody.ProblemCode());
        Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/vault/{code}", headers: P2.Key(linkKey))).Status);

        // Stored: the ciphertext is gone; the audit trail has no contents.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
        var row = await db.Secrets.AsNoTracking().SingleAsync(s => s.ResourceId == created.ResourceId());
        Assert.Null(row.Ciphertext);
        Assert.Equal("burned", row.WipeReason);

        var (ownerStatus, detail) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/vault/secrets/{created.ResourceId()}");
        Assert.Equal(HttpStatusCode.OK, ownerStatus);
        Assert.Equal("burned", detail.GetProperty("state").GetString());
        Assert.False(detail.TryGetProperty("ciphertext", out _));
        var kinds = detail.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["created", "revealed", "burned"], kinds);
    }

    [Fact]
    public async Task Parallel_reveals_succeed_exactly_max_views_times()
    {
        var free = await P.CreateUserAsync(starter: false);
        var starter = await P.CreateUserAsync(starter: true);
        foreach (var (user, views) in new[] { (free, 1), (starter, 3) })
        {
            using var owner = api.ApiClient(bearer: user.AccessToken);
            var (created, _, envelope) = await CreateAsync(owner, maxViews: views);
            var code = created.Code();
            var linkKey = created.GetProperty("linkKey").GetString()!;

            var tasks = Enumerable.Range(0, 20).Select(async _ =>
            {
                using var client = api.ApiClient();
                return await P2.SendAsync(client, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(linkKey));
            }).ToList();
            var results = await Task.WhenAll(tasks);
            Assert.Equal(views, results.Count(r => r.Status == HttpStatusCode.OK));
            Assert.All(results.Where(r => r.Status == HttpStatusCode.OK), r => Assert.Equal(envelope, r.Body.GetProperty("ciphertext").GetString()));
            Assert.Equal(20 - views, results.Count(r => r.Status == HttpStatusCode.Gone));
            Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK && r.Body.GetProperty("burned").GetBoolean()));
        }
    }

    [Fact]
    public async Task Plan_caps_and_envelope_validation()
    {
        var free = await P.CreateUserAsync(starter: false);
        var starter = await P.CreateUserAsync(starter: true);
        using var f = api.ApiClient(bearer: free.AccessToken);
        using var s = api.ApiClient(bearer: starter.AccessToken);
        var (ok, _) = VaultCrypto.Encrypt("{\"v\":1,\"content\":\"x\"}");

        // Views: Free 1 only (402), Starter up to 10, never above 100 or below 1 (400).
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(ok, maxViews: 2))).Status);
        Assert.Equal(HttpStatusCode.Created, (await P2.SendAsync(s, HttpMethod.Post, "/api/vault/secrets", Body(ok, maxViews: 10))).Status);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(s, HttpMethod.Post, "/api/vault/secrets", Body(ok, maxViews: 11))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(s, HttpMethod.Post, "/api/vault/secrets", Body(ok, maxViews: 0))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(s, HttpMethod.Post, "/api/vault/secrets", Body(ok, maxViews: 101))).Status);
        // Password: Starter only.
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(ok, password: "hunter22"))).Status);

        // Size: Free 64 KB of ciphertext.
        var (big, _) = VaultCrypto.Encrypt(new string('a', 70 * 1024));
        var (bigStatus, bigBody) = await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigStatus);
        Assert.Equal("secret-too-large", bigBody.ProblemCode());
        Assert.Equal(HttpStatusCode.Created, (await P2.SendAsync(s, HttpMethod.Post, "/api/vault/secrets", Body(big))).Status);

        // Envelope structure.
        var raw = VaultCrypto.FromBase64Url(ok);
        string Mutate(Action<byte[]> change) { var copy = raw.ToArray(); change(copy); return VaultCrypto.Base64Url(copy); }
        foreach (var bad in new[]
                 {
                     ok + "=",                                      // padding
                     ok[..10] + "+" + ok[11..],                       // not base64url
                     Mutate(b => b[0] = 0x02),                        // unknown version
                     Mutate(b => b[1] = 0x80),                        // unknown flag
                     Mutate(b => b[1] = 0x01),                        // passphrase flag without passphraseProtected
                     VaultCrypto.Base64Url(raw[..29]),                // too short
                     "",
                 })
        {
            var (status, body) = await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(bad));
            Assert.True(status == HttpStatusCode.BadRequest, $"{bad[..Math.Min(12, bad.Length)]}... -> {(int)status} {body}");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(ok, passphrase: true))).Status);
        // NUL in a string field is a 400 (global converter), control characters in the label too.
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(ok, label: "a\u0000b"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(ok, label: "a\u0007b"))).Status);

        // A passphrase envelope round-trips.
        var (pp, ppKey) = VaultCrypto.Encrypt("{\"v\":1,\"content\":\"pp\"}", passphrase: "correct horse");
        var (ppStatus, ppCreated) = await P2.SendAsync(f, HttpMethod.Post, "/api/vault/secrets", Body(pp, passphrase: true));
        Assert.Equal(HttpStatusCode.Created, ppStatus);
        using var v = api.ApiClient();
        var (_, desc) = await P2.SendAsync(v, HttpMethod.Get, $"/api/public/vault/{ppCreated.Code()}", headers: P2.Key(ppCreated.GetProperty("linkKey").GetString()));
        Assert.True(desc.GetProperty("passphraseProtected").GetBoolean());
        var (_, rev) = await P2.SendAsync(v, HttpMethod.Post, $"/api/public/vault/{ppCreated.Code()}/reveal", headers: P2.Key(ppCreated.GetProperty("linkKey").GetString()));
        Assert.Contains("pp", VaultCrypto.Decrypt(rev.GetProperty("ciphertext").GetString()!, ppKey, "correct horse"));
    }

    [Fact]
    public async Task Guests_create_one_view_secrets_and_a_failing_credential_is_401()
    {
        using var guest = api.ApiClient();
        var (created, encKey, _) = await CreateAsync(guest);
        Assert.True(created.GetProperty("isGuest").GetBoolean());
        var (env, _) = VaultCrypto.Encrypt("{\"v\":1,\"content\":\"x\"}");
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(guest, HttpMethod.Post, "/api/vault/secrets", Body(env, maxViews: 2))).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await P2.SendAsync(guest, HttpMethod.Post, "/api/vault/secrets", Body(env, expiresAt: api.Clock.GetUtcNow().UtcDateTime.AddDays(8)))).Status);

        using var broken = api.ApiClient(bearer: "not-a-valid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(broken, HttpMethod.Post, "/api/vault/secrets", Body(env))).Status);

        // Guest secrets have no owner routes.
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(guest, HttpMethod.Get, $"/api/vault/secrets/{created.ResourceId()}")).Status);
        var (_, reveal) = await P2.SendAsync(guest, HttpMethod.Post, $"/api/public/vault/{created.Code()}/reveal", headers: P2.Key(created.GetProperty("linkKey").GetString()));
        Assert.Contains("s3cr3t", VaultCrypto.Decrypt(reveal.GetProperty("ciphertext").GetString()!, encKey));
    }

    [Fact]
    public async Task Owner_routes_are_owner_only_and_destroy_wipes_at_once()
    {
        var alice = await P.CreateUserAsync(starter: false, tag: "alice");
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var a = api.ApiClient(bearer: alice.AccessToken);
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        var (created, _, _) = await CreateAsync(a);
        var id = created.ResourceId();

        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/vault/secrets/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Delete, $"/api/vault/secrets/{id}")).Status);
        Assert.DoesNotContain((await P2.SendAsync(m, HttpMethod.Get, "/api/vault/secrets")).Body.EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);
        Assert.Contains((await P2.SendAsync(a, HttpMethod.Get, "/api/vault/secrets")).Body.EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);

        // The library cannot make a secret public.
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(a, HttpMethod.Patch, $"/api/resources/{id}", new { @public = true })).Status);

        using var del = await P2.RawAsync(a, HttpMethod.Delete, $"/api/vault/secrets/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.True(del.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(a, HttpMethod.Delete, $"/api/vault/secrets/{id}")).StatusCode); // idempotent
        using var visitor = api.ApiClient();
        var (gone, goneBody) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{created.Code()}/reveal", headers: P2.Key(created.GetProperty("linkKey").GetString()));
        Assert.Equal(HttpStatusCode.Gone, gone);
        Assert.Equal("deleted", goneBody.ProblemCode());

        using var scope = api.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<DevToolsDbContext>().Secrets.AsNoTracking().SingleAsync(s => s.ResourceId == id);
        Assert.Null(row.Ciphertext);
        Assert.Equal("deleted", row.WipeReason);

        // API keys need tools:write to create and to destroy.
        var key = await P.CreateApiKeyAsync(mallory, "resources:read");
        using var k = api.ApiClient(apiKey: key);
        var (env, _) = VaultCrypto.Encrypt("{}");
        var (scopeStatus, scopeBody) = await P2.SendAsync(k, HttpMethod.Post, "/api/vault/secrets", Body(env));
        Assert.Equal(HttpStatusCode.Forbidden, scopeStatus);
        Assert.Equal("insufficient-scope", scopeBody.ProblemCode());
        var writeKey = await P.CreateApiKeyAsync(mallory, "tools:write");
        using var w = api.ApiClient(apiKey: writeKey);
        Assert.Equal(HttpStatusCode.Created, (await P2.SendAsync(w, HttpMethod.Post, "/api/vault/secrets", Body(env))).Status);
    }

    [Fact]
    public async Task Expiry_closes_the_secret_and_the_sweep_wipes_the_ciphertext()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var owner = api.ApiClient(bearer: user.AccessToken);
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var (created, _, _) = await CreateAsync(owner, expiresAt: now.AddMinutes(5));
        var linkKey = created.GetProperty("linkKey").GetString();

        api.Clock.Advance(TimeSpan.FromMinutes(6));
        try
        {
            using var visitor = api.ApiClient();
            var (status, body) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{created.Code()}/reveal", headers: P2.Key(linkKey));
            Assert.Equal(HttpStatusCode.Gone, status);
            Assert.Equal("expired", body.ProblemCode());

            await api.Services.GetRequiredService<DevToolsSweeper>().RunOnceAsync(CancellationToken.None);
            using var scope = api.Services.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<DevToolsDbContext>().Secrets.AsNoTracking().SingleAsync(s => s.ResourceId == created.ResourceId());
            Assert.Null(row.Ciphertext);
            Assert.Equal("expired", row.WipeReason);
            var (_, detail) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/vault/secrets/{created.ResourceId()}");
            Assert.Equal("expired", detail.GetProperty("state").GetString());
            // Renewal of an expired secret is refused.
            Assert.Equal(HttpStatusCode.Conflict, (await P2.SendAsync(owner, HttpMethod.Patch, $"/api/resources/{created.ResourceId()}",
                new { expiresAt = api.Clock.GetUtcNow().UtcDateTime.AddDays(1) })).Status);
        }
        finally
        {
            api.Clock.Advance(TimeSpan.FromMinutes(-6));
        }
    }

    [Fact]
    public async Task Password_gate_uses_the_phase1_unlock_grant()
    {
        var user = await P.CreateUserAsync(starter: true);
        using var owner = api.ApiClient(bearer: user.AccessToken);
        var (created, encKey, _) = await CreateAsync(owner, password: "open-sesame");
        var code = created.Code();
        var linkKey = created.GetProperty("linkKey").GetString()!;
        using var visitor = api.ApiClient();

        var (locked, lockedBody) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(linkKey));
        Assert.Equal(HttpStatusCode.Unauthorized, locked);
        Assert.Equal("password-required", lockedBody.ProblemCode());

        var (wrong, _) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/content/{code}/unlock", new { password = "nope" }, P2.Key(linkKey));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong);
        var (unlocked, grant) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/content/{code}/unlock", new { password = "open-sesame" }, P2.Key(linkKey));
        Assert.Equal(HttpStatusCode.OK, unlocked);
        var token = grant.GetProperty("grant").GetString();

        var (_, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/vault/{code}", headers: P2.Key(linkKey, token));
        Assert.True(desc.GetProperty("hasPassword").GetBoolean());
        var (ok, reveal) = await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(linkKey, token));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Contains("s3cr3t", VaultCrypto.Decrypt(reveal.GetProperty("ciphertext").GetString()!, encKey));
    }

    [Fact]
    public async Task Kinds_never_cross_and_the_short_link_redirects_to_the_vault_viewer()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var owner = api.ApiClient(bearer: user.AccessToken);
        var (secret, _, _) = await CreateAsync(owner);
        var key = secret.GetProperty("linkKey").GetString();
        using var visitor = api.ApiClient();

        // A secret is not served by the Phase 1 content descriptor, and a snippet is not served by the vault.
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/content/{secret.Code()}", headers: P2.Key(key))).Status);
        var (snipStatus, snippet) = await P2.SendAsync(owner, HttpMethod.Post, "/api/snippets", new { content = "hello" },
            new Dictionary<string, string> { ["Idempotency-Key"] = "k-" + TestIps.UniqueSuffix() });
        Assert.Equal(HttpStatusCode.Created, snipStatus);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{snippet.Code()}/reveal",
            headers: P2.Key(snippet.GetProperty("linkKey").GetString()))).Status);

        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync($"/{secret.Code()}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal($"{ZaaglApiFactory.WebBase}/v/{secret.Code()}", redirect.Headers.Location!.ToString());
        Assert.False(redirect.Headers.Contains("Set-Cookie"));

        // The owner's link list and resource library know the new kind.
        var (_, links) = await P2.SendAsync(owner, HttpMethod.Get, "/api/links?kind=secret");
        Assert.Contains(links.GetProperty("items").EnumerateArray(), l => l.GetProperty("link").GetProperty("slug").GetString() == secret.Code() && l.GetProperty("link").GetProperty("kind").GetString() == "secret");
        var (_, library) = await P2.SendAsync(owner, HttpMethod.Get, "/api/resources?kind=secret");
        Assert.Contains(library.GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == secret.ResourceId());
    }

    [Fact]
    public async Task Lifecycle_events_wipe_and_the_purge_handler_removes_the_rows()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var owner = api.ApiClient(bearer: user.AccessToken);
        var (created, _, _) = await CreateAsync(owner);
        var id = created.ResourceId();

        using var scope = api.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<SecretLifecycle>();
        var db = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
        var now = api.Clock.GetUtcNow().UtcDateTime;
        // Another kind's event is ignored; a takedown of this secret wipes it, twice is harmless (at least once delivery).
        await lifecycle.HandleAsync(new Zaagl.Modules.Links.Contracts.LinkDisabled(1, Guid.NewGuid(), id, Zaagl.Modules.Links.Contracts.LinkTargetKind.Snippet, now, "abuse"), CancellationToken.None);
        Assert.NotNull((await db.Secrets.AsNoTracking().SingleAsync(s => s.ResourceId == id)).Ciphertext);
        for (var i = 0; i < 2; i++)
            await lifecycle.HandleAsync(new Zaagl.Modules.Links.Contracts.LinkDisabled(2, Guid.NewGuid(), id, Zaagl.Modules.Links.Contracts.LinkTargetKind.Secret, now, "abuse"), CancellationToken.None);
        var row = await db.Secrets.AsNoTracking().SingleAsync(s => s.ResourceId == id);
        Assert.Null(row.Ciphertext);
        Assert.Equal("disabled", row.WipeReason);
        Assert.Equal(1, await db.SecretEvents.CountAsync(e => e.ResourceId == id && e.Kind == "disabled"));

        await lifecycle.OnResourceDeletedAsync(id, CancellationToken.None);
        Assert.False(await db.Secrets.AnyAsync(s => s.ResourceId == id));
        Assert.False(await db.SecretEvents.AnyAsync(e => e.ResourceId == id));
    }
}
