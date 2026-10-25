using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resolution.Application;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

public class ClientClassifierTests
{
    private readonly ClientClassifier _classifier = new();

    private const string Iphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
    private const string Android = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
    private const string Windows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    [Fact]
    public void Detects_ios()
    {
        var c = _classifier.Classify(Iphone);
        Assert.Equal("ios", c.Platform);
        Assert.Equal("mobile", c.DeviceType);
        Assert.False(c.IsBot);
    }

    [Fact]
    public void Detects_android()
    {
        var c = _classifier.Classify(Android);
        Assert.Equal("android", c.Platform);
        Assert.Equal("mobile", c.DeviceType);
    }

    [Fact]
    public void Detects_desktop()
    {
        var c = _classifier.Classify(Windows);
        Assert.Equal("desktop", c.Platform);
        Assert.Equal("desktop", c.DeviceType);
        Assert.False(c.IsBot);
        Assert.NotNull(c.Browser);
    }

    [Theory]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)")]
    [InlineData("WhatsApp/2.23.20.0 A")]
    [InlineData("Twitterbot/1.0")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)")]
    public void Link_preview_crawlers_are_bots_and_flagged_as_previewers(string userAgent)
    {
        var c = _classifier.Classify(userAgent);
        Assert.True(c.IsBot);
        Assert.True(c.IsPreviewCrawler);
        Assert.Equal("bot", c.DeviceType);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("curl/8.4.0")]
    [InlineData("python-requests/2.31.0")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_automated_clients_are_bots_but_not_previewers(string? userAgent)
    {
        var c = _classifier.Classify(userAgent);
        Assert.True(c.IsBot);
        Assert.False(c.IsPreviewCrawler);
    }
}

public class ResolutionHelpersTests
{
    [Theory]
    [InlineData("https://www.linkedin.com/feed/", "linkedin.com")]
    [InlineData("https://news.ycombinator.com/item?id=1", "news.ycombinator.com")]
    [InlineData("http://Example.COM", "example.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    [InlineData("android-app://com.slack", null)]
    public void Extracts_referrer_host(string? referer, string? expected) =>
        Assert.Equal(expected, ResolutionService.ReferrerHost(referer));
}

public class AnalyticsWindowTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Live_is_the_last_thirty_minutes_by_minute()
    {
        var (from, to, _) = AnalyticsQueries.ResolveWindow(AnalyticsRange.Live, null, null, null, Now);
        Assert.Equal(TimeSpan.FromMinutes(30), Now - from);
        Assert.True(to > Now);
        Assert.Equal("minute", AnalyticsQueries.GranularityFor(to - from));
    }

    [Theory]
    [InlineData(AnalyticsRange.Last24Hours, "hour")]
    [InlineData(AnalyticsRange.Last7Days, "day")]
    [InlineData(AnalyticsRange.Last30Days, "day")]
    public void Preset_ranges_pick_a_sensible_granularity(AnalyticsRange range, string expected)
    {
        var (from, to, _) = AnalyticsQueries.ResolveWindow(range, null, null, null, Now);
        Assert.Equal(expected, AnalyticsQueries.GranularityFor(to - from));
    }

    [Fact]
    public void Free_plan_retention_clamps_the_window()
    {
        var (from, _, limited) = AnalyticsQueries.ResolveWindow(AnalyticsRange.Last30Days, null, null, 7, Now);
        Assert.True(limited);
        Assert.Equal(Now.AddDays(-7), from);
    }

    [Fact]
    public void Window_within_retention_is_untouched()
    {
        var (from, _, limited) = AnalyticsQueries.ResolveWindow(AnalyticsRange.Last7Days, null, null, 365, Now);
        Assert.False(limited);
        Assert.Equal(Now.AddDays(-7), from);
    }

    [Fact]
    public void Custom_range_is_honoured_and_never_ends_in_the_future()
    {
        var (from, to, _) = AnalyticsQueries.ResolveWindow(
            AnalyticsRange.Custom, Now.AddDays(-10), Now.AddDays(5), null, Now);
        Assert.Equal(Now.AddDays(-10), from);
        Assert.True(to <= Now.AddSeconds(1));
    }

    [Fact]
    public void Custom_range_with_from_after_to_falls_back_to_one_day()
    {
        var (from, to, _) = AnalyticsQueries.ResolveWindow(
            AnalyticsRange.Custom, Now, Now.AddDays(-3), null, Now);
        Assert.True(from < to);
    }

    [Fact]
    public void Custom_range_is_capped_at_a_year()
    {
        var (from, to, _) = AnalyticsQueries.ResolveWindow(
            AnalyticsRange.Custom, Now.AddYears(-5), Now, null, Now);
        Assert.True(to - from <= TimeSpan.FromDays(366));
    }

    [Fact]
    public void Retention_never_produces_an_empty_window()
    {
        var (from, to, limited) = AnalyticsQueries.ResolveWindow(
            AnalyticsRange.Custom, Now.AddDays(-400), Now.AddDays(-300), 7, Now);
        Assert.True(limited);
        Assert.True(to > from);
    }
}

public class CsvAndValidatorTests
{
    [Fact]
    public void Parses_quoted_fields_commas_and_escaped_quotes()
    {
        var rows = BulkLinkImporter.ParseCsv("destination,title\r\nhttps://a.example,\"Hello, \"\"world\"\"\"\r\nhttps://b.example,plain\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("Hello, \"world\"", rows[1][1]);
        Assert.Equal(["https://b.example", "plain"], rows[2]);
    }

    [Fact]
    public void Parses_a_last_row_without_trailing_newline()
    {
        var rows = BulkLinkImporter.ParseCsv("a,b\n1,2");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "2"], rows[1]);
    }

    [Fact]
    public void Keeps_newlines_inside_quotes()
    {
        var rows = BulkLinkImporter.ParseCsv("a,b\n\"line1\nline2\",x\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("line1\nline2", rows[1][0]);
    }

    private readonly LinkInputValidator _validator = new(new CodeGenerator(new FeistelPermutation("test-key-0123456789")));

    [Fact]
    public void Minimal_input_is_valid() =>
        Assert.True(_validator.Validate(new LinkInput("https://example.com")).IsValid);

    [Fact]
    public void Reserved_custom_slug_is_rejected() =>
        Assert.False(_validator.Validate(new LinkInput("https://example.com", CustomSlug: "api")).IsValid);

    [Fact]
    public void Device_rule_must_match_a_known_platform()
    {
        var bad = new LinkInput("https://example.com", Rules: [new RuleInput("device", "symbian", "https://x.example")]);
        var good = new LinkInput("https://example.com", Rules: [new RuleInput("device", "ios", "https://x.example")]);
        Assert.False(_validator.Validate(bad).IsValid);
        Assert.True(_validator.Validate(good).IsValid);
    }

    [Fact]
    public void Country_rule_needs_a_two_letter_code()
    {
        var bad = new LinkInput("https://example.com", Rules: [new RuleInput("country", "EGY", "https://x.example")]);
        var good = new LinkInput("https://example.com", Rules: [new RuleInput("country", "EG", "https://x.example")]);
        Assert.False(_validator.Validate(bad).IsValid);
        Assert.True(_validator.Validate(good).IsValid);
    }

    [Fact]
    public void Click_limit_and_password_are_bounded()
    {
        Assert.False(_validator.Validate(new LinkInput("https://example.com", Policy: new PolicyInput(MaxClicks: 0))).IsValid);
        Assert.False(_validator.Validate(new LinkInput("https://example.com", Policy: new PolicyInput(Password: "abc"))).IsValid);
        Assert.True(_validator.Validate(new LinkInput("https://example.com", Policy: new PolicyInput(Password: "abcd", MaxClicks: 10))).IsValid);
    }

    [Fact]
    public void Too_many_tags_are_rejected() =>
        Assert.False(_validator.Validate(new LinkInput("https://example.com", Tags: Enumerable.Range(0, 11).Select(i => $"t{i}").ToArray())).IsValid);
}
