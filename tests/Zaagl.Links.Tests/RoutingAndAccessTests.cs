using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resolution.Application;

namespace Zaagl.Links.Tests;

internal static class Snapshots
{
    public static LinkSnapshot Make(
        IReadOnlyList<RuleDto>? rules = null,
        bool isActive = true,
        string? passwordHash = null,
        DateTime? expiresAt = null,
        int? maxClicks = null,
        string? fallback = null) => new(
            Guid.NewGuid(), Guid.NewGuid(), "abc123", "https://main.example/", isActive,
            passwordHash, expiresAt, maxClicks, fallback, null, null, null, false, rules ?? []);
}

public class RoutingEvaluatorTests
{
    private static RuleDto Device(string match, string url, int priority = 0) => new("device", match, url, 1, priority);
    private static RuleDto Country(string match, string url, int priority = 0) => new("country", match, url, 1, priority);
    private static RuleDto Split(string url, int weight) => new("split", null, url, weight, 0);

    [Fact]
    public void Uses_primary_destination_without_rules() =>
        Assert.Equal("https://main.example/", RoutingEvaluator.Choose(Snapshots.Make(), "desktop", "EG"));

    [Theory]
    [InlineData("ios", "https://appstore.example/")]
    [InlineData("android", "https://play.example/")]
    [InlineData("desktop", "https://main.example/")]
    public void Routes_by_device(string platform, string expected)
    {
        var link = Snapshots.Make([Device("ios", "https://appstore.example/"), Device("android", "https://play.example/")]);
        Assert.Equal(expected, RoutingEvaluator.Choose(link, platform, null));
    }

    [Fact]
    public void Routes_by_country_case_insensitively()
    {
        var link = Snapshots.Make([Country("EG", "https://eg.example/")]);
        Assert.Equal("https://eg.example/", RoutingEvaluator.Choose(link, "desktop", "eg"));
        Assert.Equal("https://main.example/", RoutingEvaluator.Choose(link, "desktop", "US"));
        Assert.Equal("https://main.example/", RoutingEvaluator.Choose(link, "desktop", null));
    }

    [Fact]
    public void Lower_priority_number_wins_when_several_rules_match()
    {
        var link = Snapshots.Make([Country("EG", "https://country.example/", 1), Device("ios", "https://ios.example/", 0)]);
        // The repository returns rules ordered by priority; the evaluator takes the first match.
        var ordered = link with { Rules = link.Rules.OrderBy(r => r.Priority).ToList() };
        Assert.Equal("https://ios.example/", RoutingEvaluator.Choose(ordered, "ios", "EG"));
    }

    [Fact]
    public void Device_and_country_rules_beat_split_testing()
    {
        var link = Snapshots.Make([Device("ios", "https://ios.example/"), Split("https://a.example/", 1), Split("https://b.example/", 1)]);
        Assert.Equal("https://ios.example/", RoutingEvaluator.Choose(link, "ios", null, _ => 0));
    }

    [Fact]
    public void Split_follows_weights()
    {
        var link = Snapshots.Make([Split("https://a.example/", 3), Split("https://b.example/", 1)]);
        Assert.Equal("https://a.example/", RoutingEvaluator.Choose(link, "desktop", null, _ => 0));
        Assert.Equal("https://a.example/", RoutingEvaluator.Choose(link, "desktop", null, _ => 2));
        Assert.Equal("https://b.example/", RoutingEvaluator.Choose(link, "desktop", null, _ => 3));
    }

    [Fact]
    public void Split_is_roughly_fifty_fifty_with_a_real_random_source()
    {
        var link = Snapshots.Make([Split("https://a.example/", 1), Split("https://b.example/", 1)]);
        var a = Enumerable.Range(0, 4000).Count(_ => RoutingEvaluator.Choose(link, "desktop", null) == "https://a.example/");
        Assert.InRange(a, 1700, 2300);
    }
}

public class AccessGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly AccessGate _gate = new();

    [Fact]
    public void Open_link_is_allowed() =>
        Assert.Equal(AccessOutcome.Allowed, _gate.Evaluate(Snapshots.Make(), Now));

    [Fact]
    public void Inactive_link_is_expired() =>
        Assert.Equal(AccessOutcome.Expired, _gate.Evaluate(Snapshots.Make(isActive: false), Now));

    [Fact]
    public void Link_past_its_expiry_is_expired() =>
        Assert.Equal(AccessOutcome.Expired, _gate.Evaluate(Snapshots.Make(expiresAt: Now.UtcDateTime.AddSeconds(-1)), Now));

    [Fact]
    public void Link_before_its_expiry_is_allowed() =>
        Assert.Equal(AccessOutcome.Allowed, _gate.Evaluate(Snapshots.Make(expiresAt: Now.UtcDateTime.AddHours(1)), Now));

    [Fact]
    public void Password_protected_link_is_locked() =>
        Assert.Equal(AccessOutcome.Locked, _gate.Evaluate(Snapshots.Make(passwordHash: BCrypt.Net.BCrypt.HashPassword("pw12345")), Now));

    [Fact]
    public void Expiry_wins_over_lock() =>
        Assert.Equal(AccessOutcome.Expired,
            _gate.Evaluate(Snapshots.Make(passwordHash: BCrypt.Net.BCrypt.HashPassword("pw12345"), expiresAt: Now.UtcDateTime.AddMinutes(-1)), Now));

    [Fact]
    public void Verifies_passwords()
    {
        var link = Snapshots.Make(passwordHash: BCrypt.Net.BCrypt.HashPassword("pw12345"));
        Assert.True(_gate.VerifyPassword(link, "pw12345"));
        Assert.False(_gate.VerifyPassword(link, "PW12345"));
        Assert.False(_gate.VerifyPassword(link, ""));
        Assert.False(_gate.VerifyPassword(link, null));
        Assert.False(_gate.VerifyPassword(Snapshots.Make(), "anything")); // link without a password
    }
}
