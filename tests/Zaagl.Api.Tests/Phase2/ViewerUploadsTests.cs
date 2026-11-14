using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Zaagl.Api.Tests.Harness;
using Zaagl.Files.Tests;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.2 viewers (docs/phase2/viewers-3d-lottie-audio.md): 3D, Lottie and audio go through the normal upload, scan and
/// ticket path; validated files get a viewer and an inline ticket; unsafe ones are rejected; burn files never get a viewer.</summary>
[Trait("Area", "P2-Viewers")]
public sealed class ViewerUploadsTests(Phase2MediaFactory api) : IClassFixture<Phase2MediaFactory>
{
    private TestPrincipals P => api.Principals;

    private async Task<(System.Text.Json.JsonElement Desc, HttpStatusCode Status)> DescriptorAsync(string code, string? key)
    {
        using var visitor = api.ApiClient();
        var (status, body) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/content/{code}", headers: P2.Key(key));
        return (body, status);
    }

    [Fact]
    public async Task A_glb_model_gets_the_3d_viewer_and_an_inert_inline_ticket()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        // Declared as media: re-kinded to file like every non-image (the media allowlist stays images only).
        var up = await MediaHelper.ReadyAsync(api, client, ViewerSamples.Glb(ViewerSamples.MinimalGltfJson, [1, 2, 3, 4]), "chair.glb", kind: "media");
        var (desc, status) = await DescriptorAsync(up.Code, up.LinkKey);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {desc}");
        Assert.Equal("file", desc.GetProperty("kind").GetString());
        Assert.Equal("model/gltf-binary", desc.GetProperty("mime").GetString());
        Assert.Equal("model3d", desc.GetProperty("file").GetProperty("viewer").GetString());
        var inline = desc.GetProperty("inlineUrl").GetString()!;

        using var bytes = await MediaHelper.FetchAsync(api, inline);
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("model/gltf-binary", bytes.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline", bytes.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", bytes.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("sandbox", bytes.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(bytes.Headers.Contains("Set-Cookie"));

        // The owner library shows the viewer too.
        var (_, owned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{up.ResourceId}");
        Assert.Equal("model3d", owned.GetProperty("viewer").GetString());
    }

    [Fact]
    public async Task Models_and_animations_that_reach_outside_themselves_are_rejected()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var external = ViewerSamples.Glb("{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"https://tracker.example/pixel.png\"}]}");
        var glb = await MediaHelper.ReadyAsync(api, client, external, "x.glb", kind: "file");
        var (desc, status) = await DescriptorAsync(glb.Code, glb.LinkKey);
        Assert.True(status == HttpStatusCode.UnprocessableEntity, desc.ToString());
        Assert.Equal("content-rejected", desc.ProblemCode());
        var (_, owned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{glb.ResourceId}");
        Assert.Equal("unsafe-content", owned.GetProperty("rejectedReason").GetString());

        var expression = ViewerSamples.Lottie(layers: "[{\"ty\":4,\"ks\":{\"r\":{\"a\":0,\"k\":0,\"x\":\"time*360\"}}}]");
        var lottie = await MediaHelper.ReadyAsync(api, client, Encoding.UTF8.GetBytes(expression), "anim.json", kind: "file");
        var (lottieDesc, lottieStatus) = await DescriptorAsync(lottie.Code, lottie.LinkKey);
        var (_, lottieOwned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{lottie.ResourceId}");
        Assert.True(lottieStatus == HttpStatusCode.UnprocessableEntity, $"{lottieDesc} / {lottieOwned}");
    }

    [Fact]
    public async Task Lottie_and_audio_viewers_and_plain_json_stays_a_download()
    {
        var user = await P.CreateUserAsync(starter: true);
        using var client = api.ApiClient(bearer: user.AccessToken);

        var lottie = await MediaHelper.ReadyAsync(api, client, Encoding.UTF8.GetBytes(ViewerSamples.Lottie()), "anim.json", kind: "file");
        var (ld, _) = await DescriptorAsync(lottie.Code, lottie.LinkKey);
        Assert.Equal("lottie", ld.GetProperty("file").GetProperty("viewer").GetString());
        using (var json = await MediaHelper.FetchAsync(api, ld.GetProperty("inlineUrl").GetString()!))
        {
            Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("inline", json.Content.Headers.ContentDisposition!.ToString());
        }

        var wav = await MediaHelper.ReadyAsync(api, client, ViewerSamples.Wav(), "stem.wav", kind: "file");
        var (wd, _) = await DescriptorAsync(wav.Code, wav.LinkKey);
        Assert.Equal("audio", wd.GetProperty("file").GetProperty("viewer").GetString());
        Assert.Equal("audio/wav", wd.GetProperty("mime").GetString());
        using var audioClient = api.HostClient(FilesApiFactory.UserContentHost);
        var inlineUrl = wd.GetProperty("inlineUrl").GetString()!;
        var request = new HttpRequestMessage(HttpMethod.Get, "/d/" + inlineUrl[(inlineUrl.IndexOf("/d/", StringComparison.Ordinal) + 3)..]);
        request.Headers.Range = new RangeHeaderValue(0, 99);
        using var ranged = await audioClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(100, (await ranged.Content.ReadAsByteArrayAsync()).Length);

        // JSON that is not a Lottie animation: no viewer, no inline URL, an attachment of unknown type.
        var plain = await MediaHelper.ReadyAsync(api, client, "{\"layers\":[],\"fr\":\"x\",\"ip\":0,\"op\":1}"u8.ToArray(), "data.json", kind: "file");
        var (pd, _) = await DescriptorAsync(plain.Code, plain.LinkKey);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, pd.GetProperty("file").GetProperty("viewer").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, pd.GetProperty("inlineUrl").ValueKind);
        Assert.Equal("application/octet-stream", pd.GetProperty("mime").GetString());

        // A burn file never gets a viewer or an inline ticket.
        var burn = await MediaHelper.ReadyAsync(api, client, ViewerSamples.Wav(), "once.wav", kind: "file", burn: true);
        var (bd, _) = await DescriptorAsync(burn.Code, burn.LinkKey);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bd.GetProperty("inlineUrl").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bd.GetProperty("file").GetProperty("viewer").ValueKind);
    }
}
