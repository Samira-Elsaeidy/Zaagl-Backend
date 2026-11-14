using System.Net;
using SkiaSharp;
using Zaagl.Api.Tests.Harness;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.2 before/after slider (docs/phase2/compare-slider.md): pairs of the owner's ready raster images only,
/// inline tickets in the descriptor, parts re-checked on every read, expiry capped by the parts, owner routes, kind isolation.</summary>
[Trait("Area", "P2-Compare")]
public sealed class CompareSliderTests(Phase2MediaFactory api) : IClassFixture<Phase2MediaFactory>
{
    private TestPrincipals P => api.Principals;

    private async Task<(TestUser User, HttpClient Client, Guid Before, Guid After)> OwnerWithImagesAsync(bool starter = false)
    {
        var user = await P.CreateUserAsync(starter: starter);
        var client = api.ApiClient(bearer: user.AccessToken);
        var before = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(640, 480, color: SKColors.Red), "before.png");
        var after = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(640, 480, SKEncodedImageFormat.Jpeg, SKColors.Blue), "after.jpg");
        return (user, client, before.ResourceId, after.ResourceId);
    }

    [Fact]
    public async Task A_comparison_serves_both_sides_by_inline_ticket()
    {
        var (_, client, before, after) = await OwnerWithImagesAsync();
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/compare",
            new { beforeResourceId = before, afterResourceId = after, beforeLabel = "v1", afterLabel = "v2 <b>bold</b>", title = "Logo" });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        var code = created.Code();
        var key = created.GetProperty("linkKey").GetString();
        Assert.NotNull(key);

        using var visitor = api.ApiClient();
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/compare/{code}")).Status);
        var (ok, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/compare/{code}", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("compare", desc.GetProperty("kind").GetString());
        Assert.Equal("ready", desc.GetProperty("state").GetString());
        Assert.Equal("Logo", desc.GetProperty("title").GetString());
        var b = desc.GetProperty("before");
        var a = desc.GetProperty("after");
        Assert.Equal("v1", b.GetProperty("label").GetString());
        Assert.Equal("v2 <b>bold</b>", a.GetProperty("label").GetString()); // plain text data; the UI escapes it
        Assert.Equal("image/png", b.GetProperty("mime").GetString());
        Assert.Equal("image/jpeg", a.GetProperty("mime").GetString());
        Assert.Equal(640, b.GetProperty("width").GetInt32());
        Assert.False(desc.ToString().Contains("download", StringComparison.OrdinalIgnoreCase));

        using var bytes = await MediaHelper.FetchAsync(api, b.GetProperty("inlineUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("image/png", bytes.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline", bytes.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", bytes.Headers.GetValues("X-Content-Type-Options").Single());

        // The short link sends visitors to the comparison viewer.
        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync($"/{code}");
        Assert.Equal($"{ZaaglApiFactory.WebBase}/c/{code}", redirect.Headers.Location!.ToString());

        // Kinds never cross.
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/content/{code}", headers: P2.Key(key))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Post, $"/api/public/vault/{code}/reveal", headers: P2.Key(key))).Status);
    }

    [Fact]
    public async Task Only_the_owners_ready_raster_images_can_be_paired()
    {
        var (_, client, before, after) = await OwnerWithImagesAsync();
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        var theirs = await MediaHelper.ReadyAsync(api, m, MediaHelper.Image(64, 64), "m.png");

        async Task<(HttpStatusCode Status, string Code)> Try(HttpClient c, object body)
        {
            var (s, b) = await P2.SendAsync(c, HttpMethod.Post, "/api/compare", body);
            return (s, b.ValueKind == System.Text.Json.JsonValueKind.Object ? b.ProblemCode() : "");
        }

        // Someone else's image, an unknown id: the same 422 (no existence oracle).
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Try(client, new { beforeResourceId = before, afterResourceId = theirs.ResourceId }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Try(client, new { beforeResourceId = before, afterResourceId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Try(client, new { beforeResourceId = before, afterResourceId = before })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Try(client, new { beforeResourceId = before, afterResourceId = after, beforeLabel = new string('x', 41) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Try(client, new { beforeResourceId = before, afterResourceId = after, beforeLabel = "a\u0001" })).Status);

        // Not scanned yet (processing), and a file that is not an image.
        var pending = await MediaHelper.UploadAsync(client, MediaHelper.Image(32, 32), "pending.png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Try(client, new { beforeResourceId = before, afterResourceId = pending.ResourceId })).Status);
        var doc = await MediaHelper.ReadyAsync(api, client, "%PDF-1.4 hello"u8.ToArray(), "doc.pdf", kind: "file");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Try(client, new { beforeResourceId = before, afterResourceId = doc.ResourceId })).Status);

        // Free cannot set a password; guests cannot create; an API key needs tools:write.
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Try(client, new { beforeResourceId = before, afterResourceId = after, password = "secret-pw" })).Status);
        using var guest = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Try(guest, new { beforeResourceId = before, afterResourceId = after })).Status);
    }

    [Fact]
    public async Task Parts_are_rechecked_on_every_read_and_cap_the_expiry()
    {
        var user = await P.CreateUserAsync(starter: true);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var before = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(100, 80), "b.png", expiresAt: now.AddDays(2));
        var after = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(100, 80, color: SKColors.Green), "a.png");

        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/compare",
            new { beforeResourceId = before.ResourceId, afterResourceId = after.ResourceId, @public = true });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, created.GetProperty("linkKey").ValueKind);
        Assert.True(created.GetProperty("expiresAt").GetDateTime().ToUniversalTime() <= now.AddDays(2).AddSeconds(1));

        // Delete the "after" image: the comparison stays, that side becomes unavailable (no URL).
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/resources/{after.ResourceId}")).StatusCode);
        using var visitor = api.ApiClient();
        var (_, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/compare/{created.Code()}");
        Assert.Equal("incomplete", desc.GetProperty("state").GetString());
        Assert.Equal("unavailable", desc.GetProperty("after").GetProperty("state").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, desc.GetProperty("after").GetProperty("inlineUrl").ValueKind);
        Assert.Equal("ready", desc.GetProperty("before").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Owner_routes_are_owner_only()
    {
        var (_, client, before, after) = await OwnerWithImagesAsync();
        var (_, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/compare", new { beforeResourceId = before, afterResourceId = after, beforeLabel = "old" });
        var id = created.ResourceId();
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/compare/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Patch, $"/api/compare/{id}", new { swap = true })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Delete, $"/api/compare/{id}")).Status);

        using var detail = await P2.RawAsync(client, HttpMethod.Get, $"/api/compare/{id}");
        Assert.True(detail.Headers.CacheControl!.NoStore);
        var (_, swapped) = await P2.SendAsync(client, HttpMethod.Patch, $"/api/compare/{id}", new { swap = true, afterLabel = "new" });
        Assert.Equal(after, swapped.GetProperty("beforeResourceId").GetGuid());
        Assert.Equal("new", swapped.GetProperty("before").GetProperty("label").GetString());
        Assert.Equal("old", swapped.GetProperty("after").GetProperty("label").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/compare/{id}")).StatusCode);
        using var visitor = api.ApiClient();
        var (gone, body) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/compare/{created.Code()}", headers: P2.Key(created.GetProperty("linkKey").GetString()));
        Assert.Equal(HttpStatusCode.Gone, gone);
        Assert.Equal("deleted", body.ProblemCode());
        // The images themselves are untouched.
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{before}")).Status);
    }
}
