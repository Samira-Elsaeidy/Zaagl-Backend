using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zaagl.Api.Endpoints;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resolution.Application;

namespace Zaagl.Links.Tests;

/// <summary>
/// PD-39, PD-42, PD-43, F-09 (spec §7.3): what the short host answers for every link state. The real redirect
/// endpoint and the real ResolutionService run over an in-memory link table; no database.
/// </summary>
public sealed class ShortHostStatusPageTests : IAsyncLifetime
{
    private const string Destination = "https://dest.example/page";
    private const string Fallback = "https://fallback.example/";
    private const string ReportPage = "https://app.zaa.test/report";
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    private sealed class TableResolver : ILinkResolver
    {
        public readonly Dictionary<string, LinkSnapshot> Links = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Clicks left per link id; a click-limited link not listed has none left.</summary>
        public readonly Dictionary<Guid, int> ClicksLeft = [];
        public int Consumed;

        public Task<LinkSnapshot?> GetSnapshotAsync(string slugKey, CancellationToken ct) =>
            Task.FromResult(Links.GetValueOrDefault(slugKey));

        public Task<bool> TryConsumeClickAsync(Guid linkId, CancellationToken ct)
        {
            Consumed++;
            var left = ClicksLeft.GetValueOrDefault(linkId);
            if (left <= 0) return Task.FromResult(false);
            ClicksLeft[linkId] = left - 1;
            return Task.FromResult(true);
        }

        public Task IncrementGuestClickCountAsync(Guid linkId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoRecorder : IClickRecorder
    {
        public bool TryRecord(ClickEvent click) => true;
    }

    private sealed class AllowAll : IRedirectRateLimiter
    {
        public RedirectLimitDecision TryAcquire(IPAddress? address) => RedirectLimitDecision.Allow;
        public void RecordMiss(IPAddress? address) { }
    }

    private readonly TableResolver _links = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static LinkSnapshot Link(
        string slug, string? fallback = Fallback, DateTime? expiresAt = null, int? maxClicks = null, bool isActive = true,
        DateTime? purgeAfter = null, DateTime? deletedAt = null, DateTime? disabledAt = null, string? disabledKind = null,
        string? ogTitle = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), slug, Destination, isActive, null, expiresAt, maxClicks, fallback,
            ogTitle, null, null, false, [], purgeAfter, deletedAt, disabledAt, disabledKind);

    public async Task InitializeAsync()
    {
        foreach (var link in new[]
                 {
                     Link("active1"),
                     Link("expfallback", expiresAt: Past),
                     Link("expnofallback", fallback: null, expiresAt: Past),
                     Link("deleted1", deletedAt: Past),
                     Link("disabled1", disabledAt: Past, disabledKind: "user-disabled"),
                     Link("adminoff", disabledAt: Past, disabledKind: "admin"),
                     Link("inactive1", isActive: false),
                     Link("abuse1", disabledAt: Past, disabledKind: "abuse"),
                     Link("safety1", disabledAt: Past, disabledKind: "safety"),
                     Link("purged1", expiresAt: Past.AddDays(-100), purgeAfter: Past),
                     Link("clicks1", maxClicks: 3),
                     Link("oneshot1", maxClicks: 1),
                     Link("budget1", maxClicks: 5),
                     Link("botlimit1", maxClicks: 5, ogTitle: "Shared title"),
                     Link("ogplain1", ogTitle: "Shared title"),
                 })
            _links.Links[link.Slug] = link;
        _links.ClicksLeft[_links.Links["oneshot1"].Id] = 1;
        _links.ClicksLeft[_links.Links["budget1"].Id] = 5;
        _links.ClicksLeft[_links.Links["botlimit1"].Id] = 5;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Frontend:BaseUrl"] = "https://app.zaa.test",
            [ResolutionEndpoints.ReportPageUrlKey] = ReportPage,
        }).Build();

        _app = await ApiTestHost.StartAsync(
            services =>
            {
                services.AddSingleton<IConfiguration>(config);
                services.AddSingleton<IRedirectRateLimiter, AllowAll>();
                services.AddSingleton<ILinkResolver>(_links);
                services.AddSingleton<AccessGate>();
                services.AddSingleton<ClientClassifier>();
                services.AddSingleton<IGeoLookup, NullGeoLookup>();
                services.AddSingleton<IClickRecorder, NoRecorder>();
                services.AddSingleton(Options.Create(new ResolutionOptions()));
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<ResolutionService>();
            },
            app => app.MapResolution());
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private const string Browser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private const string Googlebot = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";
    private const string Slackbot = "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)";

    private Task<HttpResponseMessage> SendAsync(string method, string slug, string userAgent = Browser)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "/" + slug);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent); // default: a human visitor
        return _client.SendAsync(request);
    }

    private static void AssertNoCookie(HttpResponseMessage response) =>
        Assert.False(response.Headers.Contains("Set-Cookie"), "The short host must never set a cookie.");

    private static async Task<string> AssertStaticPageAsync(HttpResponseMessage response, HttpStatusCode status, bool head)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(StatusPages.ContentSecurityPolicy, string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        AssertNoCookie(response);

        var body = await response.Content.ReadAsStringAsync();
        if (head)
        {
            Assert.Equal("", body);
            return body;
        }
        // No external assets: no script, no stylesheet link, no image, no src= anywhere.
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Destination, body);
        Assert.DoesNotContain(Fallback, body);
        return body;
    }

    [Fact]
    public async Task Active_link_redirects_302_with_private_max_age_0()
    {
        foreach (var method in new[] { "GET", "HEAD" })
        {
            var response = await SendAsync(method, "active1");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(Destination, response.Headers.Location?.ToString());
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.Equal(TimeSpan.Zero, response.Headers.CacheControl?.MaxAge);
            Assert.False(response.Headers.CacheControl?.NoStore);
            Assert.Equal(ResolutionEndpoints.RedirectCacheControl, "private, max-age=0");
            AssertNoCookie(response);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Expired_link_with_a_fallback_url_redirects_302_to_it(string method)
    {
        var response = await SendAsync(method, "expfallback");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(Fallback, response.Headers.Location?.ToString());
        AssertNoCookie(response);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Expired_link_without_a_fallback_gets_the_410_expired_page(string method)
    {
        var body = await AssertStaticPageAsync(await SendAsync(method, "expnofallback"), HttpStatusCode.Gone, method == "HEAD");
        if (method == "GET") Assert.Contains("has expired", body);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Deleted_link_gets_the_410_page_and_never_the_fallback(string method)
    {
        var body = await AssertStaticPageAsync(await SendAsync(method, "deleted1"), HttpStatusCode.Gone, method == "HEAD");
        if (method == "GET") Assert.Contains("no longer available", body);
    }

    [Theory]
    [InlineData("disabled1")]
    [InlineData("adminoff")]
    [InlineData("inactive1")]
    public async Task Disabled_link_gets_the_410_disabled_page_and_never_the_fallback(string slug)
    {
        var body = await AssertStaticPageAsync(await SendAsync("GET", slug), HttpStatusCode.Gone, head: false);
        Assert.Contains("has been disabled", body);
        Assert.DoesNotContain("acceptable use", body);
        await AssertStaticPageAsync(await SendAsync("HEAD", slug), HttpStatusCode.Gone, head: true);
    }

    [Theory]
    [InlineData("abuse1")]
    [InlineData("safety1")]
    public async Task Abuse_and_safety_takedowns_get_the_acceptable_use_page_linking_to_the_report_page(string slug)
    {
        var body = await AssertStaticPageAsync(await SendAsync("GET", slug), HttpStatusCode.Gone, head: false);
        Assert.Contains("acceptable use policy", body);
        Assert.Contains($"href=\"{ReportPage}\"", body);
        await AssertStaticPageAsync(await SendAsync("HEAD", slug), HttpStatusCode.Gone, head: true);
    }

    [Theory]
    [InlineData("purged1")]
    [InlineData("unknown1")]
    public async Task Purgeable_and_unknown_links_get_the_404_page(string slug)
    {
        var body = await AssertStaticPageAsync(await SendAsync("GET", slug), HttpStatusCode.NotFound, head: false);
        Assert.Contains("Link not found", body);
        await AssertStaticPageAsync(await SendAsync("HEAD", slug), HttpStatusCode.NotFound, head: true);
    }

    [Fact]
    public async Task Used_up_click_limit_gets_its_own_410_page_and_never_the_fallback()
    {
        var body = await AssertStaticPageAsync(await SendAsync("GET", "clicks1"), HttpStatusCode.Gone, head: false);
        Assert.Contains("reached its click limit", body);
        Assert.Equal(1, _links.Consumed);
    }

    [Fact]
    public async Task Head_on_a_click_limited_link_with_clicks_left_shows_no_location_and_consumes_nothing()
    {
        await AssertStaticPageAsync(await SendAsync("HEAD", "budget1"), HttpStatusCode.Gone, head: true); // PD-47
        Assert.Equal(0, _links.Consumed);

        // GET still works and is the only thing that consumes.
        var get = await SendAsync("GET", "budget1");
        Assert.Equal(HttpStatusCode.Found, get.StatusCode);
        Assert.Equal(Destination, get.Headers.Location?.ToString());
        Assert.Equal(1, _links.Consumed);
    }

    [Fact]
    public async Task A_get_uses_up_a_one_time_link_then_head_shows_no_location()
    {
        var get = await SendAsync("GET", "oneshot1");
        Assert.Equal(HttpStatusCode.Found, get.StatusCode);
        Assert.Equal(Destination, get.Headers.Location?.ToString());

        await AssertStaticPageAsync(await SendAsync("HEAD", "oneshot1"), HttpStatusCode.Gone, head: true);
        var again = await AssertStaticPageAsync(await SendAsync("GET", "oneshot1"), HttpStatusCode.Gone, head: false);
        Assert.Contains("reached its click limit", again);
    }

    [Theory] // PD-48
    [InlineData(Googlebot)]
    [InlineData(Slackbot)]
    public async Task Bot_or_preview_crawler_on_a_click_limited_link_gets_410_and_consumes_nothing(string userAgent)
    {
        var id = _links.Links["botlimit1"].Id;
        var body = await AssertStaticPageAsync(await SendAsync("GET", "botlimit1", userAgent), HttpStatusCode.Gone, head: false);
        Assert.Contains("reached its click limit", body);
        Assert.DoesNotContain("og:", body);
        Assert.Equal(0, _links.Consumed);
        Assert.Equal(5, _links.ClicksLeft[id]);

        // A real person still gets through and consumes exactly one click.
        var get = await SendAsync("GET", "botlimit1");
        Assert.Equal(HttpStatusCode.Found, get.StatusCode);
        Assert.Equal(Destination, get.Headers.Location?.ToString());
        Assert.Equal(4, _links.ClicksLeft[id]);
    }

    [Fact] // PD-48: links without a click limit keep today's bot handling and chat previews.
    public async Task Bots_and_preview_crawlers_on_an_ordinary_link_are_unchanged()
    {
        var bot = await SendAsync("GET", "ogplain1", Googlebot);
        Assert.Equal(HttpStatusCode.Found, bot.StatusCode);
        Assert.Equal(Destination, bot.Headers.Location?.ToString());

        var preview = await SendAsync("GET", "ogplain1", Slackbot);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Null(preview.Headers.Location);
        var html = await preview.Content.ReadAsStringAsync();
        Assert.Contains("og:title", html);
        Assert.Contains(Destination, html);

        // A preview crawler on an ordinary link without Open Graph tags is redirected, as before.
        var crawlerRedirect = await SendAsync("GET", "active1", Slackbot);
        Assert.Equal(HttpStatusCode.Found, crawlerRedirect.StatusCode);
        Assert.Equal(Destination, crawlerRedirect.Headers.Location?.ToString());
        Assert.Equal(0, _links.Consumed);
    }
}
