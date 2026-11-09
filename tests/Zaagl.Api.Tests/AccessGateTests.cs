using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>QA-009: AccessGate safety net over HTTP - password unlock, expiry, click-limit exactness, fallback.</summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "QA-009")]
public sealed class AccessGateTests(ZaaglApiFactory api)
{
    private TestPrincipals P => api.Principals;

    private static string Dest() => $"https://example.com/qa/gate/{TestIps.UniqueSuffix()}";
    private static string Fallback() => $"https://example.org/qa/fallback/{TestIps.UniqueSuffix()}";

    private sealed record UnlockOk(string Url);

    // ---- Password ----------------------------------------------------------------------------

    [Fact]
    public async Task Password_link_redirects_to_the_unlock_page_and_never_leaks_the_destination()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(Password: "open sesame 1")));

        using var client = api.ShortClient();
        var response = await client.GetAsync($"/{link.Slug}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{ZaaglApiFactory.WebBase}/unlock/{link.Slug}", response.Headers.Location?.ToString());
        Assert.DoesNotContain(destination, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unlock_wrong_password_is_401_right_password_is_200_with_destination()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(Password: "open sesame 2")));
        using var client = api.ApiClient();

        var wrong = await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "open sesame 3" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.DoesNotContain(destination, await wrong.Content.ReadAsStringAsync());

        var missing = await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = (string?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var right = await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "open sesame 2" });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var body = await right.Content.ReadFromJsonAsync<UnlockOk>(TestPrincipals.Json);
        Assert.Equal(destination, body!.Url);
    }

    [Fact]
    public async Task Unlock_unknown_slug_is_404()
    {
        using var client = api.ApiClient();
        var response = await client.PostAsJsonAsync($"/api/unlock/qa{TestIps.UniqueSuffix()}", new { password = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unlock_is_rate_limited_to_10_per_minute_per_address_and_link()
    {
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner, new LinkInput(Dest(), Policy: new PolicyInput(Password: "open sesame 4")));
        using var client = api.ApiClient(TestIps.RandomV4());

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "nope" })).StatusCode);

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    // ---- Expiry -------------------------------------------------------------------------------

    [Fact]
    public async Task Expired_link_goes_to_fallback_and_unlock_returns_410()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var fallback = Fallback();
        var expires = api.Clock.GetUtcNow().UtcDateTime.AddSeconds(30);
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination,
            Policy: new PolicyInput(Password: "open sesame 5", ExpiresAt: expires, FallbackUrl: fallback)));

        using var shortClient = api.ShortClient();
        using var apiClient = api.ApiClient();
        Assert.Equal(HttpStatusCode.OK, (await apiClient.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "open sesame 5" })).StatusCode);

        api.Clock.Advance(TimeSpan.FromSeconds(61));

        var get = await shortClient.GetAsync($"/{link.Slug}");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode); // PD-42: expired WITH a fallback -> 302 to the fallback
        Assert.Equal(fallback, get.Headers.Location?.ToString());

        var unlock = await apiClient.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "open sesame 5" });
        Assert.Equal(HttpStatusCode.Gone, unlock.StatusCode);
        Assert.DoesNotContain(destination, await unlock.Content.ReadAsStringAsync());
    }

    // ---- Click limits ------------------------------------------------------------------------------

    /// <summary>
    /// Exactly N of 50 simultaneous visitors pass a limit of N; PD-43: the rest get the 410 click-limit page,
    /// never the fallback (the link has one, to prove it is not used). Start barrier, several repetitions,
    /// and the authoritative counter is read back through the owner API.
    /// </summary>
    [Theory]
    [Trait("Decision", "PD-43")]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 5)]
    [InlineData(49, 2)]
    public async Task Click_limit_is_exact_under_50_parallel_requests(int limit, int repetitions)
    {
        var owner = await P.CreateUserAsync();
        for (var rep = 0; rep < repetitions; rep++)
        {
            var destination = Dest();
            var fallback = Fallback();
            var link = await P.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(MaxClicks: limit, FallbackUrl: fallback)));

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 50).Select(async _ =>
            {
                using var client = api.ShortClient(TestIps.RandomV4());
                await start.Task;
                var r = await client.GetAsync($"/{link.Slug}");
                return (r.StatusCode, Location: r.Headers.Location?.ToString());
            }).ToList();
            start.SetResult();
            var results = await Task.WhenAll(tasks);

            var passed = results.Count(r => r.StatusCode == HttpStatusCode.Redirect && r.Location == destination);
            var fellBack = results.Count(r => r.Location == fallback);
            var gone = results.Count(r => r.StatusCode == HttpStatusCode.Gone && r.Location is null);
            Assert.True(passed == limit && gone == 50 - limit && fellBack == 0,
                $"rep {rep}: limit {limit}: {passed} reached the destination, {gone} got 410, {fellBack} got the fallback; " +
                $"statuses {string.Join(",", results.GroupBy(r => (int)r.StatusCode).Select(g => $"{g.Key}x{g.Count()}"))}");

            using var apiClient = api.ApiClient(bearer: owner.AccessToken);
            var dto = JsonSerializer.Deserialize<LinkDto>(await apiClient.GetStringAsync($"/api/links/{link.Id}"), TestPrincipals.Json)!;
            Assert.Equal(limit, dto.Policy.ClicksUsed);
        }
    }

    /// <summary>PD-43: a used-up click limit shows the small static "reached its click limit" page, status 410,
    /// with the §7.3 static-page headers, and never redirects to the fallback URL (with or without one).
    /// HEAD on it: 410, no body, consumes nothing.</summary>
    [Theory]
    [Trait("Decision", "PD-43")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Used_up_click_limit_returns_410_static_page_never_the_fallback(bool withFallback)
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var fallback = withFallback ? Fallback() : null;
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(MaxClicks: 1, FallbackUrl: fallback)));
        using var client = api.ShortClient();

        Assert.Equal(destination, (await client.GetAsync($"/{link.Slug}")).Headers.Location?.ToString());

        var second = await client.GetAsync($"/{link.Slug}");
        var what = $"used-up click limit{(withFallback ? " with fallback" : "")} GET";
        Assert.True(second.StatusCode == HttpStatusCode.Gone, $"{what} -> {(int)second.StatusCode} Location={second.Headers.Location}");
        var body = await second.Content.ReadAsStringAsync();
        Assert.StartsWith("text/html", second.Content.Headers.ContentType?.MediaType ?? "");
        Assert.Contains("click limit", body, StringComparison.OrdinalIgnoreCase); // PD-43 wording: "reached its click limit"
        Assert.DoesNotContain(destination, body);
        if (fallback is not null) Assert.DoesNotContain(fallback, body);
        StaticPage.AssertHeaders(second, what);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
        Assert.Equal(HttpStatusCode.Gone, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        StaticPage.AssertHeaders(head, "HEAD " + what);

        using var apiClient = api.ApiClient(bearer: owner.AccessToken);
        var dto = JsonSerializer.Deserialize<LinkDto>(await apiClient.GetStringAsync($"/api/links/{link.Id}"), TestPrincipals.Json)!;
        Assert.Equal(1, dto.Policy.ClicksUsed);
    }

    /// <summary>PD-47 (and spec §7.3 "HEAD never counts", RD-01): HEAD on a click-limited link never reveals
    /// the destination (no Location) and never consumes a click; it answers with the click-limit page status
    /// (410) and no body, even while clicks remain. A later GET still reaches the destination.</summary>
    [Fact]
    [Trait("Decision", "PD-47")]
    [Trait("Defect", "RD-01")]
    public async Task Head_on_a_click_limited_link_is_410_without_location_and_does_not_consume_a_click()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var fallback = Fallback();
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(MaxClicks: 1, FallbackUrl: fallback)));
        using var client = api.ShortClient();

        for (var i = 0; i < 3; i++)
        {
            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
            Assert.True(head.StatusCode == HttpStatusCode.Gone, $"HEAD #{i + 1} -> {(int)head.StatusCode} Location={head.Headers.Location}");
            Assert.Null(head.Headers.Location);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.False(head.Headers.Contains("Set-Cookie"));
        }

        using var apiClient = api.ApiClient(bearer: owner.AccessToken);
        var dto = JsonSerializer.Deserialize<LinkDto>(await apiClient.GetStringAsync($"/api/links/{link.Id}"), TestPrincipals.Json)!;
        Assert.Equal(0, dto.Policy.ClicksUsed);
        Assert.Equal(destination, (await client.GetAsync($"/{link.Slug}")).Headers.Location?.ToString());
    }

    /// <summary>PD-47: ordinary links (no click limit) are unaffected: HEAD answers like GET (302 + Location,
    /// no body). "Without counting" is not observable here (no click-count read model for unlimited links);
    /// see RD-01 note in the baseline.</summary>
    [Fact]
    [Trait("Decision", "PD-47")]
    public async Task Head_on_an_ordinary_link_answers_like_get()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, destination);
        using var client = api.ShortClient();

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
        Assert.Equal(HttpStatusCode.Redirect, head.StatusCode);
        Assert.Equal(destination, head.Headers.Location?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Password_plus_click_limit_counts_successful_unlocks_only()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination,
            Policy: new PolicyInput(Password: "open sesame 6", MaxClicks: 1, FallbackUrl: Fallback())));
        using var client = api.ApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "wrong" })).StatusCode);
        var first = await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "open sesame 6" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsJsonAsync($"/api/unlock/{link.Slug}", new { password = "open sesame 6" });
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
    }
}
