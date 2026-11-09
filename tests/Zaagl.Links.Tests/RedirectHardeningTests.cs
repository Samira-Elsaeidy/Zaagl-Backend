using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zaagl.Api.Endpoints;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Resolution.Application;
using Zaagl.SharedKernel.Net;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

/// <summary>BE-031 (spec §7.3): redirect and tiered miss limits per IP partition.</summary>
public sealed class RedirectRateLimiterTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StaticOptions(RedirectRateLimitOptions value) : IOptionsMonitor<RedirectRateLimitOptions>
    {
        public RedirectRateLimitOptions CurrentValue => value;
        public RedirectRateLimitOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<RedirectRateLimitOptions, string?> listener) => null;
    }

    private static (InMemoryRedirectRateLimiter Limiter, ManualTimeProvider Time) Create(RedirectRateLimitOptions? options = null)
    {
        var time = new ManualTimeProvider();
        return (new InMemoryRedirectRateLimiter(time, new StaticOptions(options ?? new RedirectRateLimitOptions())), time);
    }

    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    [Fact]
    public void Defaults_are_the_spec_numbers()
    {
        var o = new RedirectRateLimitOptions();
        Assert.Equal((300, 30, 120, 300), (o.PerMinute, o.MissesPerMinute, o.MissesPerMinutePer56, o.MissesPerMinutePer48));
    }

    [Fact]
    public void Allows_300_redirects_per_minute_then_refuses_until_the_next_minute()
    {
        var (limiter, time) = Create();
        var ip = Ip("198.51.100.7");

        for (var i = 0; i < 300; i++) Assert.True(limiter.TryAcquire(ip).Allowed);
        var refused = limiter.TryAcquire(ip);
        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(50), refused.RetryAfter);

        // Another IPv4 address is its own partition.
        Assert.True(limiter.TryAcquire(Ip("198.51.100.8")).Allowed);

        time.Now = time.Now.AddMinutes(1);
        Assert.True(limiter.TryAcquire(ip).Allowed);
    }

    [Fact]
    public void IPv6_addresses_in_one_64_share_the_redirect_budget()
    {
        var (limiter, _) = Create(new RedirectRateLimitOptions { PerMinute = 2 });

        Assert.True(limiter.TryAcquire(Ip("2001:db8:1:2::1")).Allowed);
        Assert.True(limiter.TryAcquire(Ip("2001:db8:1:2:ffff::9")).Allowed);
        Assert.False(limiter.TryAcquire(Ip("2001:db8:1:2::abcd")).Allowed);
        Assert.True(limiter.TryAcquire(Ip("2001:db8:1:3::1")).Allowed); // next /64
    }

    [Fact]
    public void Thirty_misses_spend_the_partition_budget_and_then_every_redirect_is_refused()
    {
        var (limiter, time) = Create();
        var ip = Ip("198.51.100.7");

        for (var i = 0; i < 30; i++)
        {
            Assert.True(limiter.TryAcquire(ip).Allowed);
            limiter.RecordMiss(ip);
        }
        Assert.False(limiter.TryAcquire(ip).Allowed);
        Assert.True(limiter.TryAcquire(Ip("198.51.100.8")).Allowed);

        time.Now = time.Now.AddMinutes(1);
        Assert.True(limiter.TryAcquire(ip).Allowed);
    }

    [Fact]
    public void IPv6_misses_are_tiered_per_64_56_and_48()
    {
        var (limiter, _) = Create();

        // Four /64s inside one /56 spend 4 x 30 = 120, the /56 budget.
        for (var subnet = 0; subnet < 4; subnet++)
            for (var i = 0; i < 30; i++) limiter.RecordMiss(Ip($"2001:db8:0:{subnet:x}::1"));
        Assert.False(limiter.TryAcquire(Ip("2001:db8:0:0::2")).Allowed);   // its /64 is spent
        Assert.False(limiter.TryAcquire(Ip("2001:db8:0:ff::1")).Allowed);  // fresh /64, same /56
        Assert.True(limiter.TryAcquire(Ip("2001:db8:0:100::1")).Allowed);  // next /56, same /48

        // Spend the rest of the /48 (300) from other /56s: 180 more misses.
        for (var n = 0; n < 6; n++)
            for (var i = 0; i < 30; i++) limiter.RecordMiss(Ip($"2001:db8:0:{0x100 * (n + 1) + n:x}::1"));
        Assert.False(limiter.TryAcquire(Ip("2001:db8:0:ff00::1")).Allowed); // fresh /56, same /48
        Assert.True(limiter.TryAcquire(Ip("2001:db8:1::1")).Allowed);       // next /48
    }

    [Fact]
    public void IPv4_misses_are_tiered_per_address_and_per_24()
    {
        var (limiter, _) = Create();

        for (var host = 1; host <= 10; host++)
            for (var i = 0; i < 30; i++) limiter.RecordMiss(Ip($"203.0.113.{host}"));

        Assert.False(limiter.TryAcquire(Ip("203.0.113.200")).Allowed); // fresh address, /24 spent (300)
        Assert.True(limiter.TryAcquire(Ip("203.0.114.1")).Allowed);
    }

    [Fact]
    public void Disabled_limiter_allows_everything()
    {
        var (limiter, _) = Create(new RedirectRateLimitOptions { Enabled = false, PerMinute = 1 });
        Assert.True(limiter.TryAcquire(Ip("198.51.100.7")).Allowed);
        Assert.True(limiter.TryAcquire(Ip("198.51.100.7")).Allowed);
    }

    [Fact]
    public void V4_prefix_24_key_groups_a_24_and_ignores_IPv6()
    {
        Assert.Equal(IpPartition.V4Prefix24Key(Ip("203.0.113.9")), IpPartition.V4Prefix24Key(Ip("203.0.113.250")));
        Assert.Equal(IpPartition.V4Prefix24Key(Ip("203.0.113.9")), IpPartition.V4Prefix24Key(Ip("::ffff:203.0.113.1")));
        Assert.NotEqual(IpPartition.V4Prefix24Key(Ip("203.0.113.9")), IpPartition.V4Prefix24Key(Ip("203.0.114.9")));
        Assert.Null(IpPartition.V4Prefix24Key(Ip("2001:db8::1")));
        Assert.Null(IpPartition.V4Prefix24Key(null));
    }
}

/// <summary>BE-031: the redirect route itself - short paths never reach any I/O, limits answer 429.</summary>
public sealed class RedirectRouteTests : IAsyncLifetime
{
    private sealed class FakeLimiter : IRedirectRateLimiter
    {
        public bool Allow { get; set; } = true;
        public int Acquired;
        public RedirectLimitDecision TryAcquire(IPAddress? address)
        {
            Acquired++;
            return Allow ? RedirectLimitDecision.Allow : new(false, TimeSpan.FromSeconds(42));
        }
        public void RecordMiss(IPAddress? address) { }
    }

    private readonly FakeLimiter _limiter = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await ApiTestHost.StartAsync(
            services =>
            {
                services.AddSingleton<IRedirectRateLimiter>(_limiter);
                services.AddPlaceholder(typeof(ResolutionService));
            },
            app => app.MapResolution());
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Theory]
    [InlineData("/a")]
    [InlineData("/ab")]
    [InlineData("/abc")]
    [InlineData("/a_c")]
    [InlineData("/abcd/efgh")]
    [InlineData("/-abc")]
    public async Task Paths_that_cannot_be_a_slug_are_404_before_any_limiter_or_lookup(string path)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            var response = await _client.SendAsync(new HttpRequestMessage(method, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(0, _limiter.Acquired);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task A_four_character_slug_reaches_the_limiter_and_the_lookup(string method)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/abcd"));
        Assert.Equal(ApiTestHost.ReachedHandler, (int)response.StatusCode); // placeholder ResolutionService was called
        Assert.Equal(1, _limiter.Acquired);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Over_the_limit_answers_429_with_retry_after_and_no_lookup(string method)
    {
        _limiter.Allow = false;
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/abcd"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public void The_shortest_slugs_that_can_exist_still_match_the_route()
    {
        // Custom slugs: at least 4 characters; generated codes: at least 5. Both fit ^[A-Za-z0-9][A-Za-z0-9_-]{3,63}$.
        Assert.Equal(4, SlugRules.MinCustomLength);
        Assert.True(CodeLengths.MinLength >= 4);
        Assert.Equal(64, SlugRules.MaxLength);
        Assert.Contains("{3,63}", ResolutionEndpoints.RedirectRoute);
        Assert.True(SlugRules.IsValidFormat("ab_d"));
        Assert.False(SlugRules.IsValidFormat("abc"));
    }
}
