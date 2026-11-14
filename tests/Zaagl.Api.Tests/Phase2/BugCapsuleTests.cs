using System.Net;
using System.Text.Json;
using Zaagl.Api.Tests.Harness;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.1 bug capsules (docs/phase2/bug-capsules.md): an owned, scanned video recording plus strict, redacted logs,
/// served by descriptor with an inline ticket; schema and size limits; owner-only routes.</summary>
[Trait("Area", "P2-Capsules")]
public sealed class BugCapsuleTests(Phase2MediaFactory api) : IClassFixture<Phase2MediaFactory>
{
    private TestPrincipals P => api.Principals;

    /// <summary>A minimal ISO BMFF file: an ftyp box (isom) and an empty mdat box.</summary>
    private static byte[] Mp4()
    {
        byte[] ftyp = [0, 0, 0, 0x18, .. "ftyp"u8, .. "isom"u8, 0, 0, 2, 0, .. "isom"u8, .. "iso2"u8];
        byte[] mdat = [0, 0, 0, 0x10, .. "mdat"u8, 1, 2, 3, 4, 5, 6, 7, 8];
        return [.. ftyp, .. mdat];
    }

    private static object Logs(string? message = null) => new
    {
        v = 1,
        console = new object[]
        {
            new { t = 120, level = "error", message = message ?? "TypeError: x is undefined", source = "app.js:10:5", stack = "at f (app.js:10:5)" },
            new { t = 300, level = "log", message = "<script>alert(1)</script>" },
        },
        network = new object[]
        {
            new
            {
                t = 200, method = "POST", url = "https://api.example.com/v1/pay?token=abc123&page=2#frag", status = 500, durationMs = 87.5,
                requestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer sk_live_123", ["Content-Type"] = "application/json" },
                responseHeaders = new Dictionary<string, string> { ["Set-Cookie"] = "sid=1" },
            },
            new { t = 250, method = "GET", url = "https://cdn.example.com/a.js", status = 200 },
        },
        environment = new { userAgent = "Mozilla/5.0", url = "https://shop.example.com/cart?session=xyz", viewport = new { w = 1280, h = 720 } },
    };

    [Fact]
    public async Task A_capsule_bundles_the_recording_and_redacted_logs()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var video = await MediaHelper.ReadyAsync(api, client, Mp4(), "bug.mp4", kind: "media");
        var (status, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/capsules",
            new { recordingResourceId = video.ResourceId, logs = Logs(), title = "Checkout fails" });
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {created}");
        var key = created.GetProperty("linkKey").GetString();

        using var visitor = api.ApiClient();
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/capsules/{created.Code()}")).Status);
        var (ok, desc) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/capsules/{created.Code()}", headers: P2.Key(key));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("Checkout fails", desc.GetProperty("title").GetString());
        var summary = desc.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("consoleCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("errorCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("networkCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("failedRequestCount").GetInt32());

        var logs = desc.GetProperty("logs");
        var request = logs.GetProperty("network")[0];
        Assert.Equal("[redacted]", request.GetProperty("requestHeaders").GetProperty("Authorization").GetString());
        Assert.Equal("application/json", request.GetProperty("requestHeaders").GetProperty("Content-Type").GetString());
        Assert.Equal("[redacted]", request.GetProperty("responseHeaders").GetProperty("Set-Cookie").GetString());
        Assert.Equal("https://api.example.com/v1/pay?token=[redacted]&page=2#[redacted]", request.GetProperty("url").GetString());
        Assert.Equal("https://shop.example.com/cart?session=[redacted]", logs.GetProperty("environment").GetProperty("url").GetString());
        Assert.Equal("<script>alert(1)</script>", logs.GetProperty("console")[1].GetProperty("message").GetString()); // data only
        Assert.DoesNotContain("sk_live_123", desc.ToString());

        var recording = desc.GetProperty("recording");
        Assert.Equal("ready", recording.GetProperty("state").GetString());
        Assert.Equal("video/mp4", recording.GetProperty("mime").GetString());
        using var bytes = await MediaHelper.FetchAsync(api, recording.GetProperty("inlineUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("video/mp4", bytes.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline", bytes.Content.Headers.ContentDisposition!.ToString());

        using var shortClient = api.ShortClient();
        using var redirect = await shortClient.GetAsync($"/{created.Code()}");
        Assert.Equal($"{ZaaglApiFactory.WebBase}/b/{created.Code()}", redirect.Headers.Location!.ToString());

        // The recording deleted later: the capsule stays, the recording becomes unavailable.
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/resources/{video.ResourceId}")).StatusCode);
        var (_, after) = await P2.SendAsync(visitor, HttpMethod.Get, $"/api/public/capsules/{created.Code()}", headers: P2.Key(key));
        Assert.Equal("unavailable", after.GetProperty("recording").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("recording").GetProperty("inlineUrl").ValueKind);
    }

    [Fact]
    public async Task The_log_schema_is_strict_and_size_capped()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        async Task<(HttpStatusCode Status, string Code)> Create(object logs, Guid? recording = null)
        {
            var (s, b) = await P2.SendAsync(client, HttpMethod.Post, "/api/capsules", new { logs, recordingResourceId = recording });
            return (s, b.ValueKind == JsonValueKind.Object ? b.ProblemCode() : "");
        }

        Assert.Equal(HttpStatusCode.Created, (await Create(new { v = 1 })).Status); // logs only, no recording
        foreach (var bad in new object[]
                 {
                     new { v = 2 },
                     new { console = Array.Empty<object>() },
                     new { v = 1, extra = true },
                     new { v = 1, console = new[] { new { level = "fatal", message = "x" } } },
                     new { v = 1, console = new[] { new { level = "error", message = "nul\u0000" } } },
                     new { v = 1, console = new[] { new { level = "error", message = new string('m', 8193) } } },
                     new { v = 1, console = new[] { new { level = "error", html = "<b>" } } },
                     new { v = 1, network = new[] { new { url = "https://x", status = 1000 } } },
                     new { v = 1, network = new[] { new { url = "https://x", requestHeaders = new { a = 1 } } } },
                     new { v = 1, console = Enumerable.Range(0, 5001).Select(_ => new { level = "log" }).ToArray() },
                 })
        {
            var (status, _) = await Create(bad);
            Assert.True(status == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(bad)[..Math.Min(80, JsonSerializer.Serialize(bad).Length)]} -> {(int)status}");
        }
        // Free: 512 KB of logs.
        var big = new { v = 1, console = Enumerable.Range(0, 80).Select(_ => new { level = "log", message = new string('x', 8000) }).ToArray() };
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "logs-too-large"), await Create(big));

        // The recording must be the caller's own scanned video.
        var image = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(20, 20), "x.png");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Create(new { v = 1 }, image.ResourceId));
        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        var theirs = await MediaHelper.ReadyAsync(api, m, Mp4(), "m.mp4", kind: "file");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "resource-unusable"), await Create(new { v = 1 }, theirs.ResourceId));
    }

    [Fact]
    public async Task Owner_routes_are_owner_only()
    {
        var user = await P.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: user.AccessToken);
        var (_, created) = await P2.SendAsync(client, HttpMethod.Post, "/api/capsules", new { logs = Logs() });
        var id = created.ResourceId();
        var (_, owned) = await P2.SendAsync(client, HttpMethod.Get, $"/api/capsules/{id}");
        Assert.Equal("ready", owned.GetProperty("state").GetString());
        Assert.Equal(1, owned.GetProperty("summary").GetProperty("errorCount").GetInt32());

        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Get, $"/api/capsules/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(m, HttpMethod.Delete, $"/api/capsules/{id}")).Status);
        using var guest = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(guest, HttpMethod.Post, "/api/capsules", new { logs = Logs() })).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(client, HttpMethod.Delete, $"/api/capsules/{id}")).StatusCode);
        var (gone, body) = await P2.SendAsync(guest, HttpMethod.Get, $"/api/public/capsules/{created.Code()}", headers: P2.Key(created.GetProperty("linkKey").GetString()));
        Assert.Equal(HttpStatusCode.Gone, gone);
        Assert.Equal("deleted", body.ProblemCode());
    }
}
