using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Infrastructure;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.3 voice-over micro-briefs, the pure part: the strict pointer timeline.</summary>
[Trait("Area", "P2-Briefs")]
public sealed class BriefTimelineTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_valid_timeline_is_rebuilt_from_known_fields()
    {
        var r = BriefTimeline.Normalize(J("""{"v":1,"events":[{"t":0,"k":"point","x":0.1234567,"y":0.5},{"t":800,"k":"tap","x":1,"y":0},{"t":900,"k":"note","x":0.5,"y":0.5,"text":" here <b> "},{"t":1000,"k":"page","p":2},{"t":1000,"k":"hide"}]}"""),
            1000, document: true, maxEvents: 10);
        Assert.True(r.IsSuccess, r.Error?.Message);
        var doc = JsonDocument.Parse(r.Value.Json).RootElement;
        Assert.Equal(5, r.Value.Events);
        Assert.Equal(0.12346, doc.GetProperty("events")[0].GetProperty("x").GetDouble());
        Assert.Equal("here <b>", doc.GetProperty("events")[2].GetProperty("text").GetString());
        Assert.True(BriefTimeline.Normalize(default, 1000, false, 10).IsSuccess); // no timeline: empty
    }

    [Theory]
    [InlineData("""{"v":2,"events":[]}""")]
    [InlineData("""{"v":1,"events":[],"extra":1}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"point","x":0.5,"y":0.5,"color":"red"}]}""")]
    [InlineData("""{"v":1,"events":[{"t":-1,"k":"hide"}]}""")]
    [InlineData("""{"v":1,"events":[{"t":1001,"k":"hide"}]}""")]
    [InlineData("""{"v":1,"events":[{"t":500,"k":"hide"},{"t":400,"k":"hide"}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"point","x":1.5,"y":0.5}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"point","x":0.5}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"hide","x":0.5,"y":0.5}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"draw","x":0.5,"y":0.5}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"note","x":0.5,"y":0.5}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"note","x":0.5,"y":0.5,"text":"a\u0000b"}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"tap","x":0.5,"y":0.5,"text":"x"}]}""")]
    [InlineData("""{"v":1,"events":[{"t":0,"k":"point","x":0.5,"y":0.5,"p":1}]}""")] // an image has no pages
    [InlineData("""{"v":1,"events":[{"t":0.5,"k":"hide"}]}""")]
    [InlineData("""[1]""")]
    public void Invalid_timelines_are_refused(string json) =>
        Assert.Equal("validation", BriefTimeline.Normalize(J(json), 1000, document: false, maxEvents: 10).Error?.Code);

    [Fact]
    public void Event_count_and_size_are_capped()
    {
        var events = string.Join(',', Enumerable.Range(0, 11).Select(i => $"{{\"t\":{i},\"k\":\"hide\"}}"));
        Assert.Equal("input-too-large", BriefTimeline.Normalize(J($"{{\"v\":1,\"events\":[{events}]}}"), 1000, false, 10).Error?.Code);
        var padded = $"{{\"v\":1,\"events\":[{{\"t\":0,\"k\":\"note\",\"x\":0,\"y\":0,\"text\":\"{new string('n', 5000)}\"}}]}}";
        Assert.Equal("input-too-large", BriefTimeline.Normalize(J(padded), 1000, false, 10).Error?.Code);
    }
}

/// <summary>Phase 2 E2.3 voice-over micro-briefs over HTTP: owned audio over an owned image or PDF, one link, a listener without an
/// account, plan caps, owner-only routes, part re-checks, purge.</summary>
[Trait("Area", "P2-Briefs")]
public sealed class VoiceBriefTests(Phase2MediaFactory api) : IClassFixture<Phase2MediaFactory>
{
    private TestPrincipals P => api.Principals;

    private static byte[] Wav(int dataBytes)
    {
        var ms = new MemoryStream();
        ms.Write("RIFF"u8); ms.Write(BitConverter.GetBytes(36 + dataBytes)); ms.Write("WAVEfmt "u8);
        ms.Write(BitConverter.GetBytes(16)); ms.Write(BitConverter.GetBytes((short)1)); ms.Write(BitConverter.GetBytes((short)1));
        ms.Write(BitConverter.GetBytes(8000)); ms.Write(BitConverter.GetBytes(16000)); ms.Write(BitConverter.GetBytes((short)2));
        ms.Write(BitConverter.GetBytes((short)16)); ms.Write("data"u8); ms.Write(BitConverter.GetBytes(dataBytes)); ms.Write(new byte[dataBytes]);
        return ms.ToArray();
    }

    private static object Timeline(bool page = false) => new
    {
        v = 1,
        events = page
            ? new object[] { new { t = 0, k = "page", p = 2 }, new { t = 100, k = "point", x = 0.25, y = 0.75, p = 2 } }
            : new object[] { new { t = 0, k = "point", x = 0.1, y = 0.2 }, new { t = 1500, k = "note", x = 0.5, y = 0.5, text = "this logo" }, new { t = 2000, k = "hide" } },
    };

    [Fact]
    public async Task A_listener_without_an_account_gets_the_audio_the_image_and_the_timeline()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var image = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(40, 30), "mock.png");
        var audio = await MediaHelper.ReadyAsync(api, client, Wav(1600), "brief.wav", kind: "file");
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/briefs", new
        {
            subjectResourceId = image.ResourceId, audioResourceId = audio.ResourceId, durationMs = 2500, timeline = Timeline(), title = "Logo feedback",
        });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        var key = created.GetProperty("linkKey").GetString();

        using var listener = api.ApiClient();
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(listener, HttpMethod.Get, $"/api/public/briefs/{created.Code()}")).Status);
        var (ok, desc) = await P2.SendAsync(listener, HttpMethod.Get, $"/api/public/briefs/{created.Code()}", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("ready", desc.GetProperty("state").GetString());
        Assert.Equal(2500, desc.GetProperty("durationMs").GetInt32());
        Assert.Equal("image", desc.GetProperty("subject").GetProperty("kind").GetString());
        Assert.Equal(40, desc.GetProperty("subject").GetProperty("width").GetInt32());
        Assert.Equal("audio/wav", desc.GetProperty("audio").GetProperty("mime").GetString());
        Assert.Equal(3, desc.GetProperty("timeline").GetProperty("events").GetArrayLength());

        using (var bytes = await MediaHelper.FetchAsync(api, desc.GetProperty("audio").GetProperty("inlineUrl").GetString()!))
        {
            Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
            Assert.Equal("audio/wav", bytes.Content.Headers.ContentType!.MediaType);
        }
        using (var pic = await MediaHelper.FetchAsync(api, desc.GetProperty("subject").GetProperty("inlineUrl").GetString()!))
            Assert.Equal(HttpStatusCode.OK, pic.StatusCode);

        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync($"/{created.Code()}");
        Assert.Equal($"{ZaaglApiFactory.WebBase}/vb/{created.Code()}", redirect.Headers.Location!.ToString());

        // Owner view; the audio deleted later makes the brief incomplete (re-checked on every read).
        var id = created.ResourceId();
        var (_, owned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/briefs/{id}");
        Assert.Equal(3, owned.GetProperty("eventCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, owned.GetProperty("audio").GetProperty("inlineUrl").ValueKind); // the owner view mints nothing
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/resources/{audio.ResourceId}")).StatusCode);
        var (_, after) = await P2.SendAsync(listener, HttpMethod.Get, $"/api/public/briefs/{created.Code()}", headers: P2.Key(key));
        Assert.Equal("incomplete", after.GetProperty("state").GetString());
        Assert.Equal("unavailable", after.GetProperty("audio").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_pdf_subject_takes_pages_and_parts_must_be_owned_and_the_right_type()
    {
        var user = await P.CreateUserAsync(starter: true);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var pdf = await MediaHelper.ReadyAsync(api, client, "%PDF-1.4\n%brief\n"u8.ToArray(), "spec.pdf", kind: "file");
        var audio = await MediaHelper.ReadyAsync(api, client, Wav(800), "a.wav", kind: "file");
        var image = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(10, 10), "i.png");
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/briefs", new
        {
            subjectResourceId = pdf.ResourceId, audioResourceId = audio.ResourceId, durationMs = 60_000, timeline = Timeline(page: true), @public = true,
        });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        using var listener = api.ApiClient();
        var (_, desc) = await P2.SendAsync(listener, HttpMethod.Get, $"/api/public/briefs/{created.Code()}");
        Assert.Equal("document", desc.GetProperty("subject").GetProperty("kind").GetString());
        Assert.Equal("application/pdf", desc.GetProperty("subject").GetProperty("mime").GetString());
        using (var doc = await MediaHelper.FetchAsync(api, desc.GetProperty("subject").GetProperty("inlineUrl").GetString()!))
        {
            Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
            Assert.StartsWith("attachment", doc.Content.Headers.ContentDisposition!.ToString()); // never rendered on our origin
        }

        async Task<(HttpStatusCode, string)> Create(Guid subject, Guid audioId, int duration = 1000, object? timeline = null)
        {
            var (s, b) = await P2.SendAsync(client, HttpMethod.Post, "/api/briefs", new { subjectResourceId = subject, audioResourceId = audioId, durationMs = duration, timeline });
            return (s, b.ValueKind == JsonValueKind.Object ? b.ProblemCode() : "");
        }
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Create(audio.ResourceId, image.ResourceId)); // audio is not a subject
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Create(image.ResourceId, pdf.ResourceId)); // a PDF is not audio
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(image.ResourceId, image.ResourceId)).Item1);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(image.ResourceId, audio.ResourceId, duration: 60_001)).Item1); // Starter: 60 s
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(image.ResourceId, audio.ResourceId, timeline: Timeline(page: true))).Item1); // pages on an image
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        var theirs = await MediaHelper.ReadyAsync(api, m, Wav(400), "m.wav", kind: "file");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Create(image.ResourceId, theirs.ResourceId));
    }

    [Fact]
    public async Task Plans_caps_owner_only_routes_and_purge()
    {
        var free = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: free.AccessToken);
        var image = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(12, 12), "p.png");
        var small = await MediaHelper.ReadyAsync(api, client, Wav(800), "s.wav", kind: "file");
        var big = await MediaHelper.ReadyAsync(api, client, Wav(1_800_000), "long.wav", kind: "file"); // more than 30 s of voice can be
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(client, HttpMethod.Post, "/api/briefs",
            new { subjectResourceId = image.ResourceId, audioResourceId = small.ResourceId, durationMs = 30_001 })).Status); // Free: 30 s
        var (tooBig, body) = await P2.SendAsync(client, HttpMethod.Post, "/api/briefs",
            new { subjectResourceId = image.ResourceId, audioResourceId = big.ResourceId, durationMs = 30_000 });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), (tooBig, body.ProblemCode()));
        var many = new { v = 1, events = Enumerable.Range(0, 601).Select(i => new { t = i, k = "hide" }).ToArray() };
        var (tooMany, manyBody) = await P2.SendAsync(client, HttpMethod.Post, "/api/briefs",
            new { subjectResourceId = image.ResourceId, audioResourceId = small.ResourceId, durationMs = 30_000, timeline = many });
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "input-too-large"), (tooMany, manyBody.ProblemCode()));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(client, HttpMethod.Post, "/api/briefs",
            new { subjectResourceId = image.ResourceId, audioResourceId = small.ResourceId, durationMs = 1000, password = "secret1" })).Status);

        var (_, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/briefs",
            new { subjectResourceId = image.ResourceId, audioResourceId = small.ResourceId, durationMs = 1000 });
        var id = created.ResourceId();
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/briefs/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Delete, $"/api/briefs/{id}")).Status);
        using var anonymous = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(anonymous, HttpMethod.Post, "/api/briefs",
            new { subjectResourceId = image.ResourceId, audioResourceId = small.ResourceId, durationMs = 1000 })).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/briefs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await P2.SendAsync(anonymous, HttpMethod.Get, $"/api/public/briefs/{created.Code()}",
            headers: P2.Key(created.GetProperty("linkKey").GetString()))).Status);
        var (_, stillThere) = await P2.SendAsync(client, HttpMethod.Get, $"/api/resources/{small.ResourceId}");
        Assert.Equal("ready", stillThere.GetProperty("state").GetString()); // the recording is not touched

        await using var scope = api.Services.CreateAsyncScope();
        foreach (var handler in scope.ServiceProvider.GetServices<Zaagl.Modules.Resources.Contracts.IResourceKindHandler>()
                     .Where(h => h.Kind == Zaagl.Modules.Resources.Contracts.ResourceKind.Brief))
            await handler.OnResourceDeletedAsync(id, CancellationToken.None);
        Assert.False(await scope.ServiceProvider.GetRequiredService<CollabDbContext>().Briefs.AnyAsync(b => b.ResourceId == id));
    }
}
