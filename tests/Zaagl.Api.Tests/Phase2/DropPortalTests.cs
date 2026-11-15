using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Identity.Infrastructure;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Drop portals with every upload kind and no per-network counter in the way (the per-network cap has its own factory).</summary>
public class DropFactory : Phase2MediaFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings().Concat([new KeyValuePair<string, string>("Drops:PerIpPerHour", "100000"), new("Drops:PerIpPerDay", "100000")]);
}

/// <summary>Per-network counter of drop uploads: two per hour.</summary>
public sealed class DropIpLimitFactory : Phase2MediaFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings().Concat([new KeyValuePair<string, string>("Drops:PerIpPerHour", "2"), new("Drops:PerIpPerDay", "2")]);
}

/// <summary>The uploader side of a portal over HTTP (tus create, PATCH, complete), as a browser without an account would do it.</summary>
public static class DropHelper
{
    private static string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));

    public static async Task<(HttpStatusCode Status, string? UploadUrl, string? Token, JsonElement Body)> CreateAsync(
        HttpClient client, string code, string? linkKey, long length, string filename = "brief.png")
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, $"/api/public/drops/{code}/uploads");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", length.ToString());
        create.Headers.Add("Upload-Metadata", $"filename {B64(filename)}");
        if (linkKey is not null) create.Headers.Add("X-Zaagl-Link-Key", linkKey);
        using var response = await client.SendAsync(create);
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, response.Headers.Location?.OriginalString,
            response.Headers.TryGetValues("X-Zaagl-Drop-Token", out var t) ? t.First() : null, body);
    }

    public static async Task<HttpStatusCode> PatchAsync(HttpClient client, string url, string? token, string? linkKey, byte[] data, long offset = 0)
    {
        using var patch = new HttpRequestMessage(HttpMethod.Patch, url) { Content = new ByteArrayContent(data) };
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", offset.ToString());
        if (token is not null) patch.Headers.Add("X-Zaagl-Drop-Token", token);
        if (linkKey is not null) patch.Headers.Add("X-Zaagl-Link-Key", linkKey);
        patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        using var response = await client.SendAsync(patch);
        return response.StatusCode;
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> CompleteAsync(HttpClient client, string url, string? token, string? linkKey,
        object? body = null)
    {
        using var complete = new HttpRequestMessage(HttpMethod.Post, url + "/complete")
        {
            Content = JsonContent.Create(body ?? new { }, options: TestPrincipals.Json),
        };
        complete.Headers.Add("Idempotency-Key", "d-" + TestIps.UniqueSuffix());
        if (token is not null) complete.Headers.Add("X-Zaagl-Drop-Token", token);
        if (linkKey is not null) complete.Headers.Add("X-Zaagl-Link-Key", linkKey);
        using var response = await client.SendAsync(complete);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>Create, one PATCH, complete. Returns the receipt.</summary>
    public static async Task<JsonElement> SendFileAsync(HttpClient client, string code, string? linkKey, byte[] data, string filename = "pic.png",
        object? who = null)
    {
        var (status, url, token, body) = await CreateAsync(client, code, linkKey, data.Length, filename);
        Assert.True(status == HttpStatusCode.Created, $"drop create -> {(int)status}: {body}");
        Assert.Equal(HttpStatusCode.NoContent, await PatchAsync(client, url!, token, linkKey, data));
        var (done, receipt) = await CompleteAsync(client, url!, token, linkKey, who);
        Assert.True(done == HttpStatusCode.Accepted, $"drop complete -> {(int)done}: {receipt}");
        return receipt;
    }
}

/// <summary>Phase 2 E2.3 "Reverse Pigeon" drop-off portals (docs/phase2/drop-portals.md): anonymous tus uploads into the owner's library
/// and quota, scanned before download, per-portal caps under concurrency, owner-only management, and uploader isolation.</summary>
[Trait("Area", "P2-Drops")]
public sealed class DropPortalTests(DropFactory api) : IClassFixture<DropFactory>
{
    private TestPrincipals P => api.Principals;

    private async Task<(TestUser User, HttpClient Client, JsonElement Portal)> PortalAsync(object? body = null, bool starter = false)
    {
        var user = await P.CreateUserAsync(starter);
        var client = api.ApiClient(bearer: user.AccessToken);
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/drops", body ?? new { title = "Client assets" });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        return (user, client, created);
    }

    private static string? Key(JsonElement created) =>
        created.TryGetProperty("linkKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;

    [Fact]
    public async Task An_anonymous_upload_lands_in_the_owner_library_and_is_scanned_before_download()
    {
        var (_, owner, portal) = await PortalAsync(new { title = "Client assets", instructions = "Send the logo files", requireName = true });
        var code = portal.Code();
        var key = Key(portal);
        Assert.NotNull(key);
        Assert.EndsWith($"/drop/{code}", portal.GetProperty("shortUrl").GetString());

        var (_, before) = await P2.SendAsync(owner, HttpMethod.Get, "/api/me/storage");
        using var visitor = api.ApiClient();
        // The descriptor needs the link key; it says nothing about other uploads.
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/drops/{code}")).Status);
        var (ok, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/drops/{code}", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("open", desc.GetProperty("state").GetString());
        Assert.Equal("Send the logo files", desc.GetProperty("instructions").GetString());
        Assert.True(desc.GetProperty("requireName").GetBoolean());
        Assert.False(desc.TryGetProperty("filesReceived", out _));

        var image = MediaHelper.Image(32, 24);
        // A required name is checked before the upload is finished.
        var (status, url, token, created) = await DropHelper.CreateAsync(visitor, code, key, image.Length, "logo.png");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(token, created.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.NoContent, await DropHelper.PatchAsync(visitor, url!, token, key, image));
        Assert.Equal(HttpStatusCode.BadRequest, (await DropHelper.CompleteAsync(visitor, url!, token, key)).Status);
        // Without the token (or with another one) the session does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await DropHelper.CompleteAsync(visitor, url!, null, key, new { name = "Ann" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await DropHelper.CompleteAsync(visitor, url!, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", key, new { name = "Ann" })).Status);
        var (done, receipt) = await DropHelper.CompleteAsync(visitor, url!, token, key, new { name = "Ann Client", email = "ann@example.com", note = "v2 <b>" });
        Assert.Equal(HttpStatusCode.Accepted, done);
        Assert.Equal("received", receipt.GetProperty("status").GetString());
        Assert.False(receipt.TryGetProperty("code", out _));
        Assert.False(receipt.TryGetProperty("linkKey", out _));
        Assert.DoesNotContain("resourceId", receipt.ToString());

        // The owner's own upload routes never reach the drop session.
        var sessionId = url!.Split('/').Last();
        using (var head = new HttpRequestMessage(HttpMethod.Head, $"/api/uploads/{sessionId}"))
            Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(head)).StatusCode);

        var (_, files) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/drops/{portal.ResourceId()}/files");
        Assert.Equal(1, files.GetArrayLength());
        var file = files[0];
        Assert.Equal("Ann Client", file.GetProperty("uploaderName").GetString());
        Assert.Equal("ann@example.com", file.GetProperty("uploaderEmail").GetString());
        Assert.Equal("v2 <b>", file.GetProperty("note").GetString());
        Assert.Equal("pending", file.GetProperty("scan").GetString());
        var fileId = file.GetProperty("resourceId").GetGuid();

        // Not downloadable before the scan; the owner's quota carries the bytes.
        var (notYet, _) = await P2.SendAsync(owner, HttpMethod.Post, $"/api/resources/{fileId}/download");
        Assert.NotEqual(HttpStatusCode.OK, notYet);
        var (_, after) = await P2.SendAsync(owner, HttpMethod.Get, "/api/me/storage");
        Assert.True(after.GetProperty("usedBytes").GetInt64() + after.GetProperty("reservedBytes").GetInt64()
                    >= before.GetProperty("usedBytes").GetInt64() + image.Length);

        await MediaHelper.RunTaskAsync(api, $"scan:{fileId}");
        var (_, scanned) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/drops/{portal.ResourceId()}/files");
        Assert.Equal("clean", scanned[0].GetProperty("scan").GetString());
        var (downloaded, ticket) = await P2.SendAsync(owner, HttpMethod.Post, $"/api/resources/{fileId}/download");
        Assert.True(downloaded == HttpStatusCode.OK, $"{(int)downloaded}: {ticket}");

        // The file is an ordinary item of the owner's library; the portal card counts it.
        var (_, library) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/resources/{fileId}");
        Assert.Equal("media", library.GetProperty("kind").GetString());
        var (_, card) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/drops/{portal.ResourceId()}");
        Assert.Equal(1, card.GetProperty("filesReceived").GetInt32());
        Assert.True(card.GetProperty("accepting").GetBoolean());

        // zaa.gl/drop/<code> and zaa.gl/<code> both lead to the uploader page; another kind's code under /drop/ is a 404.
        using var shortClient = api.ShortClient();
        using (var r = await shortClient.GetAsync($"/drop/{code}"))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal($"{ZaaglApiFactory.WebBase}/drop/{code}", r.Headers.Location!.ToString());
        }
        using (var r = await shortClient.GetAsync($"/{code}"))
            Assert.Equal($"{ZaaglApiFactory.WebBase}/drop/{code}", r.Headers.Location!.ToString());
        var media = await MediaHelper.ReadyAsync(api, owner, MediaHelper.Image(8, 8), "other.png");
        using (var r = await shortClient.GetAsync($"/drop/{media.Code}"))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Portal_caps_types_and_close_are_enforced()
    {
        var (_, owner, portal) = await PortalAsync(new { maxFiles = 2, maxFileBytes = 4096, allowedTypes = new[] { "image" }, @public = true });
        var code = portal.Code();
        Assert.Null(Key(portal));
        using var visitor = api.ApiClient();

        // Per-file cap at create (before any byte).
        var (tooBig, _, _, big) = await DropHelper.CreateAsync(visitor, code, null, 5000);
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "file-too-large"), (tooBig, big.ProblemCode()));

        // A PDF into an image-only portal is refused at complete and its slot is freed.
        var pdf = "%PDF-1.4\n%test\n"u8.ToArray();
        var (s1, url1, tok1, _) = await DropHelper.CreateAsync(visitor, code, null, pdf.Length, "doc.pdf");
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.NoContent, await DropHelper.PatchAsync(visitor, url1!, tok1, null, pdf));
        var (rejected, why) = await DropHelper.CompleteAsync(visitor, url1!, tok1, null);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "content-rejected"), (rejected, why.ProblemCode()));

        // An executable is refused for drop uploads whatever the plan says.
        var exe = new byte[] { (byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0 };
        var (s2, url2, tok2, _) = await DropHelper.CreateAsync(visitor, code, null, exe.Length, "setup.png");
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.Equal(HttpStatusCode.NoContent, await DropHelper.PatchAsync(visitor, url2!, tok2, null, exe));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DropHelper.CompleteAsync(visitor, url2!, tok2, null)).Status);

        var png = MediaHelper.Image(10, 10);
        await DropHelper.SendFileAsync(visitor, code, null, png);
        await DropHelper.SendFileAsync(visitor, code, null, png);
        var fullTry = await DropHelper.CreateAsync(visitor, code, null, png.Length);
        Assert.Equal((HttpStatusCode.Conflict, "drop-closed"), (fullTry.Status, fullTry.Body.ProblemCode()));
        var (_, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/drops/{code}");
        Assert.Equal("full", desc.GetProperty("state").GetString());

        // Raising the cap reopens room; closing stops new uploads; reopening allows them again.
        var id = portal.ResourceId();
        var (patched, card) = await P2.SendAsync(owner, HttpMethod.Patch, $"/api/drops/{id}", new { maxFiles = 3 });
        Assert.Equal(HttpStatusCode.OK, patched);
        Assert.True(card.GetProperty("accepting").GetBoolean());
        var (closed, closedCard) = await P2.SendAsync(owner, HttpMethod.Post, $"/api/drops/{id}/close");
        Assert.Equal(HttpStatusCode.OK, closed);
        Assert.False(closedCard.GetProperty("accepting").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await DropHelper.CreateAsync(visitor, code, null, png.Length)).Status);
        Assert.Equal("closed", (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/drops/{code}")).Body.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(owner, HttpMethod.Post, $"/api/drops/{id}/reopen")).Status);
        await DropHelper.SendFileAsync(visitor, code, null, png);

        // Plan caps on the settings themselves (Free: 20 files).
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(owner, HttpMethod.Patch, $"/api/drops/{id}", new { maxFiles = 21 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(owner, HttpMethod.Patch, $"/api/drops/{id}", new { allowedTypes = new[] { "exe" } })).Status);
    }

    [Fact]
    public async Task Concurrent_uploads_never_pass_the_portal_file_cap()
    {
        var (_, _, portal) = await PortalAsync(new { maxFiles = 3, @public = true });
        var code = portal.Code();
        var png = MediaHelper.Image(6, 6);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var visitor = api.ApiClient();
            return (await DropHelper.CreateAsync(visitor, code, null, png.Length)).Status;
        }));
        Assert.Equal(3, attempts.Count(s => s == HttpStatusCode.Created));
        Assert.All(attempts.Where(s => s != HttpStatusCode.Created), s => Assert.True(s is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable,
            $"unexpected {(int)s}"));
    }

    [Fact]
    public async Task Concurrent_creates_never_pass_the_open_portal_cap_and_owner_routes_are_owner_only()
    {
        var user = await P.CreateUserAsync(starter: false); // Free: 2 open portals
        using var client = api.ApiClient(bearer: user.AccessToken);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            P2.SendAsync(client, HttpMethod.Post, "/api/drops", new { title = $"p{i}" })));
        Assert.Equal(2, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal("limit-reached", r.Body.ProblemCode()));
        var id = results.First(r => r.Status == HttpStatusCode.Created).Body.ResourceId();

        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"/api/drops/{id}"), (HttpMethod.Get, $"/api/drops/{id}/files"), (HttpMethod.Patch, $"/api/drops/{id}"),
                     (HttpMethod.Post, $"/api/drops/{id}/close"), (HttpMethod.Post, $"/api/drops/{id}/reopen"), (HttpMethod.Delete, $"/api/drops/{id}"),
                 })
            Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, method, url, method == HttpMethod.Patch ? new { maxFiles = 1 } : null)).Status);
        Assert.Empty((await P2.SendAsync(m, HttpMethod.Get, "/api/drops")).Body.EnumerateArray());

        using var anonymous = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(anonymous, HttpMethod.Post, "/api/drops", new { title = "x" })).Status);
        using var broken = api.ApiClient(bearer: "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(broken, HttpMethod.Get, $"/api/public/drops/anything")).Status);

        // Closing one frees a slot; reopening it while two are open is refused.
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(client, HttpMethod.Post, $"/api/drops/{id}/close")).Status);
        Assert.Equal(HttpStatusCode.Created, (await P2.SendAsync(client, HttpMethod.Post, "/api/drops", new { title = "third" })).Status);
        Assert.Equal("limit-reached", (await P2.SendAsync(client, HttpMethod.Post, $"/api/drops/{id}/reopen")).Body.ProblemCode());

        // Delete: the portal answers 410 to uploaders; the list no longer shows it.
        var (_, card) = await P2.SendAsync(client, HttpMethod.Get, $"/api/drops/{id}");
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/drops/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(anonymous, HttpMethod.Get, $"/api/public/drops/{card.GetProperty("code").GetString()}")).Status);
        Assert.DoesNotContain((await P2.SendAsync(client, HttpMethod.Get, "/api/drops")).Body.EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Owner_is_notified_once_per_window_and_attribution_goes_with_the_file()
    {
        var (user, owner, portal) = await PortalAsync(new { @public = true, notifyOwner = true });
        using var visitor = api.ApiClient();
        var png = MediaHelper.Image(5, 5);
        await DropHelper.SendFileAsync(visitor, portal.Code(), null, png, who: new { name = "<script>x</script>" });
        await DropHelper.SendFileAsync(visitor, portal.Code(), null, png);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var notices = await identity.EmailOutbox.AsNoTracking().Where(e => e.UserId == user.Id && e.Kind == "drop-received").ToListAsync();
            Assert.Single(notices);
            Assert.DoesNotContain("script", notices[0].PayloadJson); // the uploader's text never reaches the email
        }

        // Deleting a received file removes its attribution row when Resources reclaims it (kind handler).
        var (_, files) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/drops/{portal.ResourceId()}/files");
        Assert.Equal(2, files.GetArrayLength());
        var fileId = files[0].GetProperty("resourceId").GetGuid();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            foreach (var handler in scope.ServiceProvider.GetServices<Zaagl.Modules.Resources.Contracts.IResourceKindHandler>()
                         .Where(h => h.Kind == Zaagl.Modules.Resources.Contracts.ResourceKind.Media))
                await handler.OnResourceDeletedAsync(fileId, CancellationToken.None);
            var collab = scope.ServiceProvider.GetRequiredService<CollabDbContext>();
            Assert.False(await collab.DropFiles.AnyAsync(f => f.ResourceId == fileId));
            foreach (var handler in scope.ServiceProvider.GetServices<Zaagl.Modules.Resources.Contracts.IResourceKindHandler>()
                         .Where(h => h.Kind == Zaagl.Modules.Resources.Contracts.ResourceKind.Drop))
                await handler.OnResourceDeletedAsync(portal.ResourceId(), CancellationToken.None);
            Assert.False(await collab.DropPortals.AnyAsync(p => p.ResourceId == portal.ResourceId()));
            Assert.False(await collab.DropFiles.AnyAsync(f => f.PortalId == portal.ResourceId()));
        }
    }

    [Fact]
    public async Task An_expired_portal_refuses_uploads_and_an_admitted_upload_can_be_aborted()
    {
        var (_, _, portal) = await PortalAsync(new { @public = true, expiresAt = DateTime.UtcNow.AddMinutes(30) });
        var code = portal.Code();
        using var visitor = api.ApiClient();
        var png = MediaHelper.Image(4, 4);
        var (s, url, token, _) = await DropHelper.CreateAsync(visitor, code, null, png.Length);
        Assert.Equal(HttpStatusCode.Created, s);
        using (var del = new HttpRequestMessage(HttpMethod.Delete, url))
        {
            del.Headers.Add("Tus-Resumable", "1.0.0");
            del.Headers.Add("X-Zaagl-Drop-Token", token);
            Assert.Equal(HttpStatusCode.NoContent, (await visitor.SendAsync(del)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Gone, await DropHelper.PatchAsync(visitor, url!, token, null, png)); // tus: terminated

        api.Clock.Advance(TimeSpan.FromHours(1));
        try
        {
            Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/drops/{code}")).Status);
            Assert.Equal(HttpStatusCode.Gone, (await DropHelper.CreateAsync(visitor, code, null, png.Length)).Status);
        }
        finally
        {
            api.Clock.Advance(-TimeSpan.FromHours(1));
        }
    }

    [Fact]
    public async Task Plans_and_validation()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        foreach (var bad in new object[]
                 {
                     new { maxFiles = 0 }, new { maxFiles = 21 }, new { maxBytes = 300L * 1024 * 1024 }, new { allowedTypes = new[] { "html" } },
                     new { instructions = new string('i', 2001) }, new { title = "bad\u0001" }, new { password = "secret1" },
                 })
        {
            var (status, body) = await P2.SendAsync(client, HttpMethod.Post, "/api/drops", bad);
            Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.PaymentRequired, $"{JsonSerializer.Serialize(bad)} -> {(int)status} {body}");
        }
        var starter = await P.CreateUserAsync(starter: true);
        using var s = api.ApiClient(bearer: starter.AccessToken);
        var (ok, created) = await P2.SendAsync(s, HttpMethod.Post, "/api/drops", new { maxFiles = 500, password = "secret1" });
        Assert.True(ok == HttpStatusCode.Created, created.ToString());
        // A password-protected portal needs the grant (the same unlock flow as any content).
        using var visitor = api.ApiClient();
        var (locked, lockedBody) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/drops/{created.Code()}", headers: P2.Key(Key(created)));
        Assert.Equal(HttpStatusCode.Unauthorized, locked);
        Assert.Equal("password-required", lockedBody.ProblemCode());
    }
}

/// <summary>The per-network counter of drop uploads (separate from the PD-14 guest content counters).</summary>
[Trait("Area", "P2-Drops")]
public sealed class DropIpLimitTests(DropIpLimitFactory api) : IClassFixture<DropIpLimitFactory>
{
    [Fact]
    public async Task One_network_can_start_only_its_hourly_number_of_uploads()
    {
        var user = await api.Principals.CreateUserAsync(starter: true);
        using var owner = api.ApiClient(bearer: user.AccessToken);
        var (_, portal) = await P2.SendAsync(owner, HttpMethod.Post, "/api/drops", new { @public = true });
        var ip = TestIps.RandomV4();
        using var visitor = api.ApiClient(ip);
        Assert.Equal(HttpStatusCode.Created, (await DropHelper.CreateAsync(visitor, portal.Code(), null, 10)).Status);
        Assert.Equal(HttpStatusCode.Created, (await DropHelper.CreateAsync(visitor, portal.Code(), null, 10)).Status);
        var (limited, _, _, body) = await DropHelper.CreateAsync(visitor, portal.Code(), null, 10);
        Assert.Equal((HttpStatusCode.TooManyRequests, "quota"), (limited, body.ProblemCode()));
        using var other = api.ApiClient(TestIps.RandomV4());
        Assert.Equal(HttpStatusCode.Created, (await DropHelper.CreateAsync(other, portal.Code(), null, 10)).Status);
    }
}
