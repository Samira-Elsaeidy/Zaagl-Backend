using System.Net;
using System.Text.Json;
using SkiaSharp;
using Zaagl.Api.Tests.Harness;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>A low per-resource comment cap, so the cap race is cheap to test.</summary>
public sealed class CommentsFactory : Phase2MediaFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        base.ExtraSettings().Concat([new KeyValuePair<string, string>("Comments:MaxPerResource", "5")]);
}

/// <summary>Phase 2 E2.2 loupe and pinned comments (docs/phase2/palette-and-comments.md): the palette in the descriptors, the owner's
/// opt-in, gate-scoped reading and posting, anonymous rules, validation, moderation, owner-only routes and the cap under concurrency.</summary>
[Trait("Area", "P2-Comments")]
public sealed class PaletteAndCommentsTests(CommentsFactory api) : IClassFixture<CommentsFactory>
{
    private TestPrincipals P => api.Principals;

    private async Task<(TestUser Owner, HttpClient Client, Guid Id, string Code, string? Key)> ImageAsync()
    {
        var owner = await P.CreateUserAsync(starter: false);
        var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(300, 200, color: new SKColor(200, 30, 30)), "art.png");
        return (owner, client, up.ResourceId, up.Code, up.LinkKey);
    }

    private static object Pin(double x = 0.5, double y = 0.5, string body = "Move this up", string? side = null, string? name = null) =>
        new { x, y, body, side, authorName = name };

    [Fact]
    public async Task The_descriptor_carries_dimensions_and_a_palette()
    {
        var (_, client, id, code, key) = await ImageAsync();
        using var visitor = api.ApiClient();
        var (status, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/content/{code}", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, status);
        var media = desc.GetProperty("media");
        Assert.Equal(300, media.GetProperty("width").GetInt32());
        Assert.Equal(200, media.GetProperty("height").GetInt32());
        var palette = media.GetProperty("palette");
        Assert.Equal(JsonValueKind.Array, palette.ValueKind);
        // The thumbnail is re-encoded, so allow a small colour drift around rgb(200, 30, 30).
        Assert.InRange(palette[0].GetProperty("r").GetInt32(), 196, 204);
        Assert.InRange(palette[0].GetProperty("g").GetInt32(), 26, 34);
        Assert.InRange(palette[0].GetProperty("b").GetInt32(), 26, 34);
        Assert.Matches("^#[0-9A-F]{6}$", palette[0].GetProperty("hex").GetString()!);
        var (_, owned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{id}");
        Assert.Equal(palette[0].GetProperty("hex").GetString(), owned.GetProperty("media").GetProperty("palette")[0].GetProperty("hex").GetString());
    }

    [Fact]
    public async Task Comments_need_the_owners_opt_in_and_the_link()
    {
        var (_, owner, id, code, key) = await ImageAsync();
        using var anonymous = api.ApiClient();

        // Off by default: readable as disabled, posting refused.
        var (_, off) = await P2.SendAsync(anonymous, HttpMethod.Get, $"/api/public/comments/{code}", headers: P2.Key(key));
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await P2.SendAsync(anonymous, HttpMethod.Post, $"/api/public/comments/{code}", Pin(), P2.Key(key))).Status);

        var (setStatus, settings) = await P2.SendAsync(owner, HttpMethod.Put, $"/api/resources/{id}/comments/settings", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, setStatus);
        Assert.False(settings.GetProperty("allowAnonymous").GetBoolean());

        // Without the link key: the same 404 as an unknown code. Anonymous not allowed yet: 401.
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(anonymous, HttpMethod.Get, $"/api/public/comments/{code}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(anonymous, HttpMethod.Post, $"/api/public/comments/{code}", Pin())).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(anonymous, HttpMethod.Post, $"/api/public/comments/{code}", Pin(), P2.Key(key))).Status);

        // The owner and a signed-in visitor may post.
        var (ownerPost, ownerPin) = await P2.SendAsync(owner, HttpMethod.Post, $"/api/public/comments/{code}", Pin(0.1, 0.2, "owner note", name: "ignored"), P2.Key(key));
        Assert.Equal(HttpStatusCode.Created, ownerPost);
        Assert.Equal("owner", ownerPin.GetProperty("author").GetString());
        Assert.Equal(JsonValueKind.Null, ownerPin.GetProperty("authorName").ValueKind);
        var client = await P.CreateUserAsync(starter: false, tag: "client");
        using var c = api.ApiClient(bearer: client.AccessToken);
        var (clientPost, clientPin) = await P2.SendAsync(c, HttpMethod.Post, $"/api/public/comments/{code}",
            Pin(0.75, 0.3333333, "<img src=x onerror=alert(1)> make it bluer", name: "Dana"), P2.Key(key));
        Assert.Equal(HttpStatusCode.Created, clientPost);
        Assert.Equal("visitor", clientPin.GetProperty("author").GetString());
        Assert.Equal(0.3333, clientPin.GetProperty("y").GetDouble());
        Assert.Equal("<img src=x onerror=alert(1)> make it bluer", clientPin.GetProperty("body").GetString()); // data, escaped by the UI
        Assert.True(clientPin.GetProperty("mine").GetBoolean());

        // Anonymous once allowed (the harness CAPTCHA verifier passes).
        await P2.SendAsync(owner, HttpMethod.Put, $"/api/resources/{id}/comments/settings", new { enabled = true, allowAnonymous = true });
        var (anonPost, anonPin) = await P2.SendAsync(anonymous, HttpMethod.Post, $"/api/public/comments/{code}", Pin(1, 0, "edge"), P2.Key(key));
        Assert.Equal(HttpStatusCode.Created, anonPost);
        Assert.Equal("anonymous", anonPin.GetProperty("author").GetString());

        var (_, thread) = await P2.SendAsync(anonymous, HttpMethod.Get, $"/api/public/comments/{code}", headers: P2.Key(key));
        Assert.Equal(3, thread.GetProperty("count").GetInt32());
        Assert.All(thread.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("mine").GetBoolean()));

        // Validation.
        foreach (var bad in new object[]
                 {
                     Pin(x: 1.01), Pin(y: -0.1), Pin(body: ""), Pin(body: new string('a', 1001)), Pin(body: "bell\u0007"),
                     Pin(side: "before"), Pin(name: new string('n', 61)),
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(c, HttpMethod.Post, $"/api/public/comments/{code}", bad, P2.Key(key))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(c, HttpMethod.Post, $"/api/public/comments/{code}", Pin(body: "nul\u0000"), P2.Key(key))).Status);

        // Moderation: the author deletes their own; nobody else's.
        var clientPinId = clientPin.GetProperty("id").GetInt64();
        var ownerPinId = ownerPin.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(c, HttpMethod.Delete, $"/api/public/comments/{code}/{ownerPinId}", headers: P2.Key(key))).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(c, HttpMethod.Delete, $"/api/public/comments/{code}/{clientPinId}", headers: P2.Key(key))).StatusCode);

        var (resolved, resolvedPin) = await P2.SendAsync(owner, HttpMethod.Post, $"/api/resources/{id}/comments/{ownerPinId}/resolve");
        Assert.Equal(HttpStatusCode.OK, resolved);
        Assert.Equal("resolved", resolvedPin.GetProperty("status").GetString());
        Assert.Equal("open", (await P2.SendAsync(owner, HttpMethod.Post, $"/api/resources/{id}/comments/{ownerPinId}/reopen")).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(owner, HttpMethod.Delete, $"/api/resources/{id}/comments/{anonPin.GetProperty("id").GetInt64()}")).StatusCode);
        var (_, ownerThread) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/resources/{id}/comments");
        Assert.Equal(1, ownerThread.GetProperty("count").GetInt32());

        // Owner routes are owner only.
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"/api/resources/{id}/comments"), (HttpMethod.Put, $"/api/resources/{id}/comments/settings"),
                     (HttpMethod.Post, $"/api/resources/{id}/comments/{ownerPinId}/resolve"), (HttpMethod.Delete, $"/api/resources/{id}/comments/{ownerPinId}"),
                 })
        {
            var (status, _) = await P2.SendAsync(c, method, url, method == HttpMethod.Put ? new { enabled = false } : null);
            Assert.True(status == HttpStatusCode.NotFound, $"{method} {url} -> {(int)status}");
        }
    }

    [Fact]
    public async Task The_per_resource_cap_holds_under_concurrency()
    {
        var (_, owner, id, code, key) = await ImageAsync();
        await P2.SendAsync(owner, HttpMethod.Put, $"/api/resources/{id}/comments/settings", new { enabled = true, allowAnonymous = true });
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var c = api.ApiClient();
            return (await P2.SendAsync(c, HttpMethod.Post, $"/api/public/comments/{code}", Pin(i / 20.0, 0.5, $"pin {i}"), P2.Key(key))).Status;
        }));
        Assert.Equal(5, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(s => s == HttpStatusCode.Conflict));
        var (_, thread) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/resources/{id}/comments");
        Assert.Equal(5, thread.GetProperty("items").GetArrayLength());
        Assert.Equal(5, thread.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Comments_on_a_comparison_name_a_side()
    {
        var owner = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var a = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(50, 50), "a.png");
        var b = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(50, 50, color: SKColors.Gold), "b.png");
        var (_, compare) = await P2.SendAsync(client, HttpMethod.Post, "/api/compare", new { beforeResourceId = a.ResourceId, afterResourceId = b.ResourceId });
        var key = compare.GetProperty("linkKey").GetString();
        await P2.SendAsync(client, HttpMethod.Put, $"/api/resources/{compare.ResourceId()}/comments/settings", new { enabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Post, $"/api/public/comments/{compare.Code()}", Pin(), P2.Key(key))).Status);
        var (status, pin) = await P2.SendAsync(client, HttpMethod.Post, $"/api/public/comments/{compare.Code()}", Pin(side: "after"), P2.Key(key));
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("after", pin.GetProperty("side").GetString());
    }
}
