using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Api.Tests;

/// <summary>
/// QA-008: redirect endpoint safety net over HTTP (spec §7.3, §1.2).
/// Traits: "Pins"="current" marks tests that pin TODAY's behaviour where it differs from the spec (flip them
/// when the spec behaviour lands); "SpecGap" marks spec-oracle tests expected to fail today;
/// "Defect" marks spec-oracle tests for a filed defect.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "QA-008")]
public sealed class RedirectEndpointTests(ZaaglApiFactory api)
{
    private TestPrincipals P => api.Principals;

    private static string Dest() => $"https://example.com/qa/redirect/{TestIps.UniqueSuffix()}";

    private static string RandomMissCode()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        Span<char> c = stackalloc char[12];
        for (var i = 0; i < c.Length; i++) c[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return "qa" + new string(c);
    }

    // ---- Active link ----------------------------------------------------------------------

    [Fact]
    public async Task Active_link_returns_302_with_location_never_301()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, destination);

        using var client = api.ShortClient();
        for (var i = 0; i < 3; i++) // repeat: a cached hit must also be 302
        {
            var response = await client.GetAsync($"/{link.Slug}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
            Assert.Equal(destination, response.Headers.Location?.ToString());
            Assert.NotNull(response.Headers.CacheControl);
        }
    }

    [Fact]
    [Trait("Defect", "F-09")]
    public async Task Active_link_cache_control_is_private_max_age_0_per_spec()
    {
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner);
        using var client = api.ShortClient();
        var response = await client.GetAsync($"/{link.Slug}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private, $"Cache-Control was '{response.Headers.CacheControl}'");
        Assert.Equal(TimeSpan.Zero, response.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task Slug_lookup_is_case_insensitive_and_unknown_code_is_404()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, destination);
        using var client = api.ShortClient();

        var upper = await client.GetAsync($"/{link.Slug.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.Redirect, upper.StatusCode);
        Assert.Equal(destination, upper.Headers.Location?.ToString());

        var miss = await client.GetAsync($"/{RandomMissCode()}");
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
    }

    // ---- HEAD ------------------------------------------------------------------------------

    [Fact]
    public async Task Head_returns_same_status_as_get_without_a_body()
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var link = await P.CreateLinkAsync(owner, destination);
        using var client = api.ShortClient();

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
        Assert.Equal(HttpStatusCode.Redirect, head.StatusCode);
        Assert.Equal(destination, head.Headers.Location?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        var headMiss = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{RandomMissCode()}"));
        Assert.Equal(HttpStatusCode.NotFound, headMiss.StatusCode);
        Assert.Empty(await headMiss.Content.ReadAsByteArrayAsync());
    }

    // ---- No cookies on the short host --------------------------------------------------------

    [Fact]
    public async Task Short_host_never_sets_cookies()
    {
        var owner = await P.CreateUserAsync();
        var active = await P.CreateLinkAsync(owner);
        var locked = await P.CreateLinkAsync(owner, new LinkInput(Dest(), Policy: new PolicyInput(Password: "correct horse")));
        var limited = await P.CreateLinkAsync(owner, new LinkInput(Dest(), Policy: new PolicyInput(MaxClicks: 1)));
        using var client = api.ShortClient();

        var requests = new List<HttpRequestMessage>
        {
            new(HttpMethod.Get, $"/{active.Slug}"),
            new(HttpMethod.Head, $"/{active.Slug}"),
            new(HttpMethod.Get, $"/{locked.Slug}"),
            new(HttpMethod.Get, $"/{limited.Slug}"),
            new(HttpMethod.Get, $"/{limited.Slug}"), // limit reached
            new(HttpMethod.Get, $"/{RandomMissCode()}"),
            new(HttpMethod.Get, "/ab"),
            new(HttpMethod.Get, "/api/links"), // misdirected (421)
        };
        foreach (var request in requests)
        {
            var path = request.RequestUri;
            var response = await client.SendAsync(request);
            Assert.False(response.Headers.Contains("Set-Cookie"), $"{request.Method} {path} -> {(int)response.StatusCode} set a cookie");
        }
    }

    // ---- Non-active states: today's behaviour (pinned) and the spec's (gap) ----------------------

    public enum NonActive { Disabled, Deleted, Expired }

    private async Task<(LinkDto Link, string Destination, string? Fallback)> MakeNonActiveAsync(NonActive state, bool withFallback)
    {
        var owner = await P.CreateUserAsync();
        var destination = Dest();
        var fallback = withFallback ? $"https://example.org/qa/fallback/{TestIps.UniqueSuffix()}" : null;
        var expires = state == NonActive.Expired ? api.Clock.GetUtcNow().UtcDateTime.AddSeconds(30) : (DateTime?)null;
        var link = await P.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(ExpiresAt: expires, FallbackUrl: fallback)));
        using var apiClient = api.ApiClient(bearer: owner.AccessToken);
        switch (state)
        {
            case NonActive.Disabled:
                var patch = await apiClient.PatchAsJsonAsync($"/api/links/{link.Id}", new LinkFlagsInput(IsActive: false));
                Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
                break;
            case NonActive.Deleted:
                var delete = await apiClient.DeleteAsync($"/api/links/{link.Id}");
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
                break;
            case NonActive.Expired:
                api.Clock.Advance(TimeSpan.FromSeconds(61));
                break;
        }
        return (link, destination, fallback);
    }

    // PD-39 + PD-42 (spec §7.3, replaces the old Pins=current / SpecGap=7.3 pair):
    //  - expired WITH a fallback URL -> 302 to the fallback;
    //  - expired without a fallback, disabled (fallback or not), deleted (fallback or not) -> 410 static page;
    //  - purgeable or unknown -> 404 static page;
    //  - HEAD: same status, no body; the static page carries CSP, nosniff and Referrer-Policy; never Set-Cookie.

    private static void AssertStaticPageHeaders(HttpResponseMessage r, string what) => StaticPage.AssertHeaders(r, what);

    [Fact]
    [Trait("Decision", "PD-42")]
    public async Task Expired_link_with_fallback_redirects_302_to_the_fallback()
    {
        var (link, destination, fallback) = await MakeNonActiveAsync(NonActive.Expired, withFallback: true);
        using var client = api.ShortClient();

        var get = await client.GetAsync($"/{link.Slug}");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal(fallback, get.Headers.Location?.ToString());
        Assert.NotEqual(destination, get.Headers.Location?.ToString());
        Assert.False(get.Headers.Contains("Set-Cookie"));

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
        Assert.Equal(HttpStatusCode.Redirect, head.StatusCode);
        Assert.Equal(fallback, head.Headers.Location?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [Trait("Decision", "PD-39")]
    [Trait("Decision", "PD-42")]
    [InlineData(NonActive.Expired, false)]
    [InlineData(NonActive.Disabled, false)]
    [InlineData(NonActive.Disabled, true)]  // PD-42: disabled never uses the fallback
    [InlineData(NonActive.Deleted, false)]
    [InlineData(NonActive.Deleted, true)]   // PD-42: deleted never uses the fallback
    public async Task Non_active_link_returns_410_static_page(NonActive state, bool withFallback)
    {
        var (link, destination, fallback) = await MakeNonActiveAsync(state, withFallback);
        using var client = api.ShortClient();
        var what = $"{state}{(withFallback ? " with fallback" : "")} GET /{link.Slug}";

        var get = await client.GetAsync($"/{link.Slug}");
        var body = await get.Content.ReadAsStringAsync();
        Assert.True(get.StatusCode == HttpStatusCode.Gone,
            $"{what} -> {(int)get.StatusCode} Location={get.Headers.Location}; PD-39/§7.3 requires 410 static page");
        Assert.StartsWith("text/html", get.Content.Headers.ContentType?.MediaType ?? "");
        Assert.NotEmpty(body);
        Assert.DoesNotContain(destination, body);
        if (fallback is not null) Assert.DoesNotContain(fallback, body);
        AssertStaticPageHeaders(get, what);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
        Assert.Equal(HttpStatusCode.Gone, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        AssertStaticPageHeaders(head, "HEAD " + what);
    }

    [Fact]
    [Trait("Decision", "PD-39")]
    public async Task Purgeable_link_returns_404_static_page()
    {
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner, new LinkInput(Dest(),
            Policy: new PolicyInput(FallbackUrl: $"https://example.org/qa/fallback/{TestIps.UniqueSuffix()}")));
        // Direct write on this test's own new row (no product path reaches purgeable without the 90-day
        // quarantine): purge_after in the past. Done before the first GET, so no snapshot is cached yet.
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            var past = api.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
            var rows = await db.Links.Where(l => l.Id == link.Id && l.OwnerId == owner.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.PurgeAfter, past));
            Assert.Equal(1, rows);
        }
        using var client = api.ShortClient();

        var get = await client.GetAsync($"/{link.Slug}");
        Assert.True(get.StatusCode == HttpStatusCode.NotFound, $"purgeable GET -> {(int)get.StatusCode} Location={get.Headers.Location}");
        AssertStaticPageHeaders(get, "purgeable GET");

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Slug}"));
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    [Trait("Decision", "PD-39")]
    public async Task Unknown_code_returns_404_static_page()
    {
        using var client = api.ShortClient();
        var get = await client.GetAsync($"/{RandomMissCode()}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        AssertStaticPageHeaders(get, "unknown code GET");
    }

    // ---- Reserved words, short and malformed paths ----------------------------------------------

    [Theory]
    [InlineData("unlock")]
    [InlineData("expired")]
    [InlineData("dashboard")]
    [InlineData("admin")]
    [InlineData("login")]
    [InlineData("pricing")]
    [InlineData("status")]
    [InlineData("www")]
    [InlineData("report")]
    [InlineData("download")]
    public async Task Reserved_words_return_404_on_the_short_host(string word)
    {
        using var client = api.ShortClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/{word}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{word}"))).StatusCode);
    }

    [Theory]
    [InlineData("/a")]
    [InlineData("/ab")]
    [InlineData("/Z9")]
    [InlineData("/-abc")]
    [InlineData("/abcd.efg")]
    [InlineData("/0123456789012345678901234567890123456789012345678901234567890123x")] // 65 characters
    public async Task One_two_character_and_malformed_paths_return_404(string path)
    {
        using var client = api.ShortClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Multi_segment_paths_return_404_even_when_the_first_segment_is_a_live_code()
    {
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner);
        using var client = api.ShortClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/{link.Slug}/extra")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/x/{link.Slug}")).StatusCode);
    }

    /// <summary>
    /// Spec §7.3 / §4.5 rule 6: a 1-3 character segment is rejected (404) before any I/O. Oracle: a live
    /// 3-character row exists in the database (inserted directly by this test - the product no longer lets
    /// anyone create one), so a 302 proves the path reached the lookup. Expected to FAIL until BE-031.
    /// </summary>
    [Fact]
    [Trait("Defect", "BE-031")]
    [Trait("Defect", "F-08")]
    public async Task Three_character_path_returns_404_without_lookup_even_if_a_row_exists()
    {
        var owner = await P.CreateUserAsync();
        var (id, slug) = await InsertThreeCharacterRowAsync(owner.Id);
        try
        {
            using var client = api.ShortClient();
            var response = await client.GetAsync($"/{slug}");
            Assert.True(response.StatusCode == HttpStatusCode.NotFound,
                $"GET /{slug} (3 characters, live row present) -> {(int)response.StatusCode} {response.Headers.Location}; spec §7.3 requires 404 before I/O.");
        }
        finally
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            await db.Links.Where(l => l.Id == id && l.OwnerId == owner.Id).ExecuteDeleteAsync();
        }
    }

    private async Task<(Guid Id, string Slug)> InsertThreeCharacterRowAsync(Guid ownerId)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var slug = new string(Enumerable.Range(0, 3).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
            if (!char.IsAsciiLetterOrDigit(slug[0])) continue;
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            if (await db.Links.AnyAsync(l => l.SlugKey == slug)) continue;
            var link = new Link
            {
                OwnerId = ownerId,
                Slug = slug,
                SlugKey = slug,
                DestinationUrl = $"https://example.com/qa/three/{TestIps.UniqueSuffix()}",
                TargetHost = "example.com",
                IsCustom = true,
                Title = "qa-api three-character legacy row",
            };
            db.Links.Add(link);
            try
            {
                await db.SaveChangesAsync();
                return (link.Id, slug);
            }
            catch (DbUpdateException)
            {
                // lost a race for that slug; try another
            }
        }
        throw new InvalidOperationException("Could not find a free 3-character slug.");
    }

    // ---- Host routing (spec §1.2) -----------------------------------------------------------------

    [Fact]
    public async Task Host_routing_redirects_only_on_short_host_api_only_on_api_host_other_hosts_421()
    {
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner);

        using var shortClient = api.ShortClient();
        using var apiClient = api.ApiClient(bearer: owner.AccessToken);
        using var otherClient = api.HostClient("evil.example");

        Assert.Equal(HttpStatusCode.Redirect, (await shortClient.GetAsync($"/{link.Slug}")).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await apiClient.GetAsync($"/{link.Slug}")).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await shortClient.GetAsync("/api/links")).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await shortClient.GetAsync("/account/confirm-email")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/api/links")).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await otherClient.GetAsync($"/{link.Slug}")).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await otherClient.GetAsync("/api/links")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/health/live")).StatusCode); // /health is exempt
    }

    [Fact]
    public async Task Unlock_api_is_not_served_on_the_short_host()
    {
        using var shortClient = api.ShortClient();
        var response = await shortClient.PostAsJsonAsync("/api/unlock/anything", new { password = "x" });
        Assert.Equal((HttpStatusCode)421, response.StatusCode);
    }
}

/// <summary>Spec §7.3 / addendum §17 note 1: the short host's small static pages (404, 410 expired/disabled,
/// 410 click limit per PD-43).</summary>
public static class StaticPage
{
    public const string Csp = "default-src 'none'; style-src 'unsafe-inline'";

    public static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(", ", v)
        : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(", ", c) : null;

    /// <summary>CSP, nosniff, Referrer-Policy, no cookie, no Location.</summary>
    public static void AssertHeaders(HttpResponseMessage r, string what)
    {
        Assert.True(Header(r, "Content-Security-Policy") == Csp, $"{what}: CSP was '{Header(r, "Content-Security-Policy")}'");
        Assert.True(Header(r, "X-Content-Type-Options") == "nosniff", $"{what}: X-Content-Type-Options was '{Header(r, "X-Content-Type-Options")}'");
        Assert.True(Header(r, "Referrer-Policy") == "no-referrer", $"{what}: Referrer-Policy was '{Header(r, "Referrer-Policy")}'");
        Assert.False(r.Headers.Contains("Set-Cookie"), $"{what}: set a cookie");
        Assert.True(r.Headers.Location is null, $"{what}: Location was '{r.Headers.Location}'");
    }
}
