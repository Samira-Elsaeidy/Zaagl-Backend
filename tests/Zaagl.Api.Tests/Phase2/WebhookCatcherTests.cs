using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.DeveloperTools.Application;
using Zaagl.Modules.DeveloperTools.Infrastructure;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Phase 2 E2.1 webhook catcher (docs/phase2/webhook-catcher.md): ingest on the short host only, inert fixed response,
/// stored request fidelity and sanitising, plan caps (live catchers, body, per-minute window, stored count) under concurrency,
/// owner-only inspector, live stream, expiry and purge.</summary>
[Trait("Area", "P2-Webhook")]
public sealed class WebhookCatcherTests(Phase2Factory api) : IClassFixture<Phase2Factory>
{
    private TestPrincipals P => api.Principals;

    private async Task<(TestUser User, HttpClient Owner, JsonElement Hook)> CatcherAsync(bool starter = false, object? body = null)
    {
        var user = await P.CreateUserAsync(starter: starter);
        var owner = api.ApiClient(bearer: user.AccessToken);
        var (status, hook) = await P2.SendAsync(owner, HttpMethod.Post, "/api/hooks", body ?? new { label = "qa" });
        Assert.True(status == HttpStatusCode.Created, $"create -> {(int)status}: {hook}");
        return (user, owner, hook);
    }

    private static string PathOf(JsonElement hook) => new Uri(hook.GetProperty("ingestUrl").GetString()!).AbsolutePath;

    [Fact]
    public async Task Ingest_stores_a_faithful_sanitised_copy_and_answers_inertly()
    {
        var (_, owner, hook) = await CatcherAsync();
        var ingest = hook.GetProperty("ingestUrl").GetString()!;
        Assert.StartsWith($"http://{ZaaglApiFactory.ShortHost}/hook/", ingest);
        Assert.Equal("live", hook.GetProperty("state").GetString());

        using var sender = api.ShortClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, PathOf(hook) + "/github/events?delivery=42&x=%C3%A9")
        {
            Content = new StringContent("{\"action\":\"opened\",\"n\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Hub-Signature-256", "sha256=abc");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        using var response = await sender.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Location"));

        var (listStatus, page) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{hook.GetProperty("id").GetGuid()}/requests");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.False(item.TryGetProperty("bodyBase64", out _));
        var (_, detail) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{hook.GetProperty("id").GetGuid()}/requests/{item.GetProperty("id").GetInt64()}");
        Assert.Equal("POST", detail.GetProperty("method").GetString());
        Assert.Equal("/github/events", detail.GetProperty("path").GetString());
        Assert.Equal("delivery=42&x=%C3%A9", detail.GetProperty("query").GetString());
        Assert.Equal("{\"action\":\"opened\",\"n\":1}", detail.GetProperty("bodyText").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"action\":\"opened\",\"n\":1}")), detail.GetProperty("bodyBase64").GetString());
        var headers = detail.GetProperty("headers").EnumerateArray().Select(h => (h[0].GetString()!, h[1].GetString()!)).ToList();
        Assert.Contains(headers, h => h.Item1 == "X-Hub-Signature-256" && h.Item2 == "sha256=abc");
        Assert.DoesNotContain(headers, h => h.Item1.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(headers, h => h.Item1.Equals("X-Test-Remote-Ip", StringComparison.OrdinalIgnoreCase));
        Assert.Matches("^[0-9a-f]{16}$", detail.GetProperty("sourceFingerprint").GetString()!);

        // Binary bodies: base64 only, no text.
        using var bin = new ByteArrayContent([0x00, 0xFF, 0x10, 0x41]);
        Assert.Equal(HttpStatusCode.OK, (await sender.PutAsync(PathOf(hook), bin)).StatusCode);
        var (_, page2) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{hook.GetProperty("id").GetGuid()}/requests");
        var newest = page2.GetProperty("items")[0];
        var (_, binDetail) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{hook.GetProperty("id").GetGuid()}/requests/{newest.GetProperty("id").GetInt64()}");
        Assert.Equal(JsonValueKind.Null, binDetail.GetProperty("bodyText").ValueKind);
        Assert.Equal("AP8QQQ==", binDetail.GetProperty("bodyBase64").GetString());

        // A NUL in the query never reaches the database (400 at the edge, never a 500).
        using var nul = await sender.GetAsync(PathOf(hook) + "/a?b=%00");
        Assert.Equal(HttpStatusCode.BadRequest, nul.StatusCode);
    }

    [Fact]
    public async Task Routing_tokens_and_the_configured_status()
    {
        var (_, owner, hook) = await CatcherAsync(body: new { responseStatus = 202 });
        var path = PathOf(hook);
        var token = path.Split('/')[2];
        using var sender = api.ShortClient();
        Assert.Equal(HttpStatusCode.Accepted, (await sender.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await sender.SendAsync(new HttpRequestMessage(HttpMethod.Head, path))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await sender.SendAsync(new HttpRequestMessage(HttpMethod.Options, path + "/x"))).StatusCode);

        // Unknown, malformed and case-variant tokens are one answer: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsync("/hook/AAAAAAAAAAAAAAAAAAAAAA", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsync("/hook/short", null)).StatusCode);
        var flipped = new string(token.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
        if (flipped != token) Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsync($"/hook/{flipped}", null)).StatusCode);

        // The ingest path exists only on the short host.
        using var apiHost = api.ApiClient();
        Assert.Equal((HttpStatusCode)421, (await apiHost.PostAsync(path, null)).StatusCode);

        // Redirect statuses can never be configured (no open redirect from zaa.gl).
        var id = hook.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(owner, HttpMethod.Patch, $"/api/hooks/{id}", new { responseStatus = 302 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await P2.SendAsync(owner, HttpMethod.Patch, $"/api/hooks/{id}", new { responseStatus = 101 })).Status);
        var (patched, patchedBody) = await P2.SendAsync(owner, HttpMethod.Patch, $"/api/hooks/{id}", new { responseStatus = 418, label = "renamed" });
        Assert.Equal(HttpStatusCode.OK, patched);
        Assert.Equal("renamed", patchedBody.GetProperty("label").GetString());
        Assert.Equal((HttpStatusCode)418, (await sender.PostAsync(path, null)).StatusCode);
    }

    [Fact]
    public async Task Plan_caps_live_catchers_lifetime_and_body_size()
    {
        var (user, owner, hook) = await CatcherAsync();
        // Free: one live catcher; the cap holds under parallel creates.
        var (second, secondBody) = await P2.SendAsync(owner, HttpMethod.Post, "/api/hooks", new { });
        Assert.Equal(HttpStatusCode.Conflict, second);
        Assert.Equal("limit-reached", secondBody.ProblemCode());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await P2.SendAsync(owner, HttpMethod.Post, "/api/hooks", new { lifetimeHours = 25 })).Status);

        var starter = await P.CreateUserAsync(starter: true);
        using var s = api.ApiClient(bearer: starter.AccessToken);
        var creates = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => P2.SendAsync(s, HttpMethod.Post, "/api/hooks", new { lifetimeHours = 168 })));
        Assert.Equal(10, creates.Count(c => c.Status == HttpStatusCode.Created));
        Assert.Equal(4, creates.Count(c => c.Status == HttpStatusCode.Conflict));

        // Guests cannot create catchers; an API key needs tools:write.
        using var guest = api.ApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await P2.SendAsync(guest, HttpMethod.Post, "/api/hooks", new { })).Status);
        var readKey = await P.CreateApiKeyAsync(starter, "resources:read");
        using var k = api.ApiClient(apiKey: readKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await P2.SendAsync(k, HttpMethod.Post, "/api/hooks", new { })).Status);

        // Body: Free stores 64 KB and counts the rest; above the 1 MiB ingest ceiling the request is refused.
        using var sender = api.ShortClient();
        var big = new byte[100 * 1024];
        Array.Fill(big, (byte)'z');
        Assert.Equal(HttpStatusCode.OK, (await sender.PostAsync(PathOf(hook), new ByteArrayContent(big))).StatusCode);
        var (_, page) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{hook.GetProperty("id").GetGuid()}/requests");
        var item = page.GetProperty("items")[0];
        Assert.True(item.GetProperty("bodyTruncated").GetBoolean());
        Assert.Equal(big.Length, item.GetProperty("bodyBytes").GetInt64());
        var (_, detail) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{hook.GetProperty("id").GetGuid()}/requests/{item.GetProperty("id").GetInt64()}");
        Assert.Equal(64 * 1024, Convert.FromBase64String(detail.GetProperty("bodyBase64").GetString()!).Length);

        using var huge = new ByteArrayContent(new byte[1024 * 1024 + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await sender.PostAsync(PathOf(hook), huge)).StatusCode);
        _ = user;
    }

    [Fact]
    public async Task Per_minute_window_holds_under_concurrency_and_old_captures_are_trimmed()
    {
        var (_, owner, hook) = await CatcherAsync(); // Free: 30 per minute, 100 kept
        var id = hook.GetProperty("id").GetGuid();
        var path = PathOf(hook);

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            using var c = api.ShortClient();
            using var r = await c.PostAsync(path, new StringContent($"n={i}"));
            return (r.StatusCode, Retry: r.Headers.RetryAfter);
        }));
        Assert.Equal(30, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.All(results.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests), r => Assert.NotNull(r.Retry));
        var (_, after) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}");
        Assert.Equal(30, after.GetProperty("storedCount").GetInt32());
        Assert.Equal(30, after.GetProperty("receivedTotal").GetInt64());

        // Four more windows: 120 accepted in total, only the newest 100 are kept.
        var advanced = TimeSpan.Zero;
        try
        {
            for (var w = 0; w < 3; w++)
            {
                api.Clock.Advance(TimeSpan.FromSeconds(61));
                advanced += TimeSpan.FromSeconds(61);
                using var c = api.ShortClient();
                for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await c.PostAsync(path, new StringContent("w"))).StatusCode);
            }
            var (_, full) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}");
            Assert.Equal(120, full.GetProperty("receivedTotal").GetInt64());
            Assert.Equal(100, full.GetProperty("storedCount").GetInt32());
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
            Assert.Equal(100, await db.HookRequests.CountAsync(r => r.HookId == id));
            Assert.Equal(full.GetProperty("storedBytes").GetInt64(), await db.HookRequests.Where(r => r.HookId == id).SumAsync(r => (long)r.StoredBytes));
        }
        finally
        {
            api.Clock.Advance(-advanced);
        }
    }

    [Fact]
    public async Task Inspector_is_owner_only_and_deletes_are_immediate()
    {
        var (_, owner, hook) = await CatcherAsync();
        var id = hook.GetProperty("id").GetGuid();
        using var sender = api.ShortClient();
        for (var i = 0; i < 3; i++) await sender.PostAsync(PathOf(hook), new StringContent($"{i}"));
        var (_, page) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}/requests?limit=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        var next = page.GetProperty("nextBefore").GetInt64();
        var (_, page2) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}/requests?before={next}");
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        var firstId = page.GetProperty("items")[0].GetProperty("id").GetInt64();

        var mallory = await P.CreateUserAsync(starter: true, tag: "mallory");
        using var m = api.ApiClient(bearer: mallory.AccessToken);
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"/api/hooks/{id}"), (HttpMethod.Get, $"/api/hooks/{id}/requests"),
                     (HttpMethod.Get, $"/api/hooks/{id}/requests/{firstId}"), (HttpMethod.Patch, $"/api/hooks/{id}"),
                     (HttpMethod.Delete, $"/api/hooks/{id}/requests/{firstId}"), (HttpMethod.Delete, $"/api/hooks/{id}/requests"),
                     (HttpMethod.Delete, $"/api/hooks/{id}"), (HttpMethod.Get, $"/api/hooks/{id}/stream"),
                 })
        {
            var (status, _) = await P2.SendAsync(m, method, url, method == HttpMethod.Patch ? new { label = "x" } : null);
            Assert.True(status == HttpStatusCode.NotFound, $"{method} {url} -> {(int)status}");
        }
        Assert.Empty((await P2.SendAsync(m, HttpMethod.Get, "/api/hooks")).Body.EnumerateArray());

        using var one = await P2.RawAsync(owner, HttpMethod.Delete, $"/api/hooks/{id}/requests/{firstId}");
        Assert.Equal(HttpStatusCode.NoContent, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}/requests/{firstId}")).Status);
        Assert.Equal(2, (await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}")).Body.GetProperty("storedCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await P2.RawAsync(owner, HttpMethod.Delete, $"/api/hooks/{id}/requests")).StatusCode);
        Assert.Equal(0, (await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}")).Body.GetProperty("storedCount").GetInt32());

        using var gone = await P2.RawAsync(owner, HttpMethod.Delete, $"/api/hooks/{id}");
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        Assert.True(gone.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsync(PathOf(hook), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}")).Status);
    }

    [Fact]
    public async Task Expiry_stops_ingest_keeps_a_read_only_grace_then_the_sweep_purges()
    {
        var (_, owner, hook) = await CatcherAsync(); // Free: 24 h
        var id = hook.GetProperty("id").GetGuid();
        using var sender = api.ShortClient();
        Assert.Equal(HttpStatusCode.OK, (await sender.PostAsync(PathOf(hook), new StringContent("before"))).StatusCode);

        var advanced = TimeSpan.Zero;
        try
        {
            api.Clock.Advance(TimeSpan.FromHours(25)); advanced += TimeSpan.FromHours(25);
            Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsync(PathOf(hook), new StringContent("after"))).StatusCode);
            var (_, readOnly) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}");
            Assert.Equal("read-only", readOnly.GetProperty("state").GetString());
            Assert.Equal(1, (await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}/requests")).Body.GetProperty("items").GetArrayLength());
            // An expired catcher no longer counts toward the live cap.
            Assert.Equal(HttpStatusCode.Created, (await P2.SendAsync(owner, HttpMethod.Post, "/api/hooks", new { })).Status);

            api.Clock.Advance(TimeSpan.FromHours(24)); advanced += TimeSpan.FromHours(24);
            Assert.Equal(HttpStatusCode.NotFound, (await P2.SendAsync(owner, HttpMethod.Get, $"/api/hooks/{id}")).Status);
            await api.Services.GetRequiredService<DevToolsSweeper>().RunOnceAsync(CancellationToken.None);
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DevToolsDbContext>();
            Assert.False(await db.Hooks.AnyAsync(h => h.Id == id));
            Assert.False(await db.HookRequests.AnyAsync(r => r.HookId == id));
        }
        finally
        {
            api.Clock.Advance(-advanced);
        }
    }

    [Fact]
    public async Task Live_stream_pushes_new_captures()
    {
        var (_, owner, hook) = await CatcherAsync();
        var id = hook.GetProperty("id").GetGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/hooks/{id}/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        using var sender = api.ShortClient();
        Assert.Equal(HttpStatusCode.OK, (await sender.PostAsync(PathOf(hook) + "/live", new StringContent("ping"), cts.Token)).StatusCode);

        string? data = null;
        while (data is null)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"/live\"")) data = line[6..];
        }
        Assert.NotNull(data);
        var evt = JsonDocument.Parse(data!).RootElement;
        Assert.Equal("POST", evt.GetProperty("method").GetString());
        Assert.False(evt.TryGetProperty("bodyBase64", out _));
        cts.Cancel();
    }
}
