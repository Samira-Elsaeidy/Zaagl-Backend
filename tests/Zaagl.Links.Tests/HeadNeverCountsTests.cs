using Microsoft.Extensions.Options;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resolution.Application;

namespace Zaagl.Links.Tests;

/// <summary>RD-01, spec §7.3 "HEAD never counts": a HEAD neither consumes a click of a click-limited link nor
/// records analytics; a GET still does both.</summary>
public sealed class HeadNeverCountsTests
{
    private const string Browser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly VisitorRequest Visitor = new("203.0.113.7", Browser, null, null);

    private sealed class CountingResolver(LinkSnapshot link) : ILinkResolver
    {
        public int Consumed;
        public int GuestIncrements;
        public Task<LinkSnapshot?> GetSnapshotAsync(string slugKey, CancellationToken cancellationToken) => Task.FromResult<LinkSnapshot?>(link);
        public Task<bool> TryConsumeClickAsync(Guid linkId, CancellationToken cancellationToken)
        {
            Consumed++;
            return Task.FromResult(Consumed <= (link.MaxClicks ?? int.MaxValue));
        }
        public Task IncrementGuestClickCountAsync(Guid linkId, CancellationToken cancellationToken)
        {
            GuestIncrements++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingRecorder : IClickRecorder
    {
        public int Recorded;
        public bool TryRecord(ClickEvent click)
        {
            Recorded++;
            return true;
        }
    }

    private static LinkSnapshot Link(Guid? owner, int? maxClicks = 1, string? ogTitle = null) => new(
        Guid.NewGuid(), owner, "abcde", "https://example.com/dest", true, null, null, maxClicks, "https://example.com/fallback",
        ogTitle, null, null, false, []);

    private static ResolutionService Service(ILinkResolver links, IClickRecorder clicks) =>
        new(links, new AccessGate(), new ClientClassifier(), new NullGeoLookup(), clicks,
            Options.Create(new ResolutionOptions()), TimeProvider.System);

    [Fact]
    public async Task Head_does_not_consume_a_click_or_record_analytics()
    {
        var resolver = new CountingResolver(Link(Guid.NewGuid()));
        var recorder = new CountingRecorder();
        var service = Service(resolver, recorder);

        // PD-47: HEAD on a click-limited link never reveals the destination, even with clicks left.
        for (var i = 0; i < 3; i++)
        {
            var head = await service.ResolveAsync("abcde", Visitor, CancellationToken.None, countsAsClick: false);
            Assert.Equal(ResolveKind.ClickLimitReached, head.Kind);
            Assert.Null(head.Url);
        }
        Assert.Equal(0, resolver.Consumed);
        Assert.Equal(0, recorder.Recorded);

        // The one click is still there for the first GET, which consumes it and records it.
        var get = await service.ResolveAsync("abcde", Visitor, CancellationToken.None);
        Assert.Equal(ResolveKind.Redirect, get.Kind);
        Assert.Equal(1, resolver.Consumed);
        Assert.Equal(1, recorder.Recorded);

        // PD-43: a used-up click limit has its own outcome and never carries the fallback URL.
        var second = await service.ResolveAsync("abcde", Visitor, CancellationToken.None);
        Assert.Equal(ResolveKind.ClickLimitReached, second.Kind);
        Assert.Null(second.Url);

        // A used-up one-time link never shows the destination to HEAD either.
        var headAfter = await service.ResolveAsync("abcde", Visitor, CancellationToken.None, countsAsClick: false);
        Assert.Equal(ResolveKind.ClickLimitReached, headAfter.Kind);
        Assert.Null(headAfter.Url);
        Assert.Equal(2, resolver.Consumed);
    }

    [Fact]
    public async Task Head_on_a_link_without_a_click_limit_answers_like_get_without_counting()
    {
        var resolver = new CountingResolver(Link(Guid.NewGuid(), maxClicks: null));
        var recorder = new CountingRecorder();
        var head = await Service(resolver, recorder).ResolveAsync("abcde", Visitor, CancellationToken.None, countsAsClick: false);

        Assert.Equal(ResolveKind.Redirect, head.Kind);
        Assert.Equal("https://example.com/dest", head.Url);
        Assert.Equal(0, recorder.Recorded);
    }

    [Fact]
    public async Task Head_from_a_preview_crawler_on_a_click_limited_link_gets_no_preview()
    {
        var resolver = new CountingResolver(Link(Guid.NewGuid(), maxClicks: 5, ogTitle: "Title"));
        var crawler = Visitor with { UserAgent = "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)" };
        var head = await Service(resolver, new CountingRecorder()).ResolveAsync("abcde", crawler, CancellationToken.None, countsAsClick: false);

        Assert.Equal(ResolveKind.ClickLimitReached, head.Kind);
        Assert.Null(head.Preview);
        Assert.Equal(0, resolver.Consumed);
    }

    [Fact]
    public async Task Head_on_a_guest_link_does_not_bump_its_click_counter()
    {
        var resolver = new CountingResolver(Link(owner: null, maxClicks: null));
        var service = Service(resolver, new CountingRecorder());

        await service.ResolveAsync("abcde", Visitor, CancellationToken.None, countsAsClick: false);
        Assert.Equal(0, resolver.GuestIncrements);

        await service.ResolveAsync("abcde", Visitor, CancellationToken.None);
        Assert.Equal(1, resolver.GuestIncrements);
    }

    [Fact]
    public async Task Head_from_a_preview_crawler_is_not_recorded()
    {
        var resolver = new CountingResolver(Link(Guid.NewGuid(), maxClicks: null, ogTitle: "Title"));
        var recorder = new CountingRecorder();
        var service = Service(resolver, recorder);
        var crawler = Visitor with { UserAgent = "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)" };

        var head = await service.ResolveAsync("abcde", crawler, CancellationToken.None, countsAsClick: false);
        Assert.Equal(ResolveKind.Preview, head.Kind);
        Assert.Equal(0, recorder.Recorded);
    }
}
