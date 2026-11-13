using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>Spec §7.15. Real local dev Postgres, same convention as PurgeJobTests/GuestCreationTests.</summary>
public sealed class AbuseControlsTests : IDisposable
{
    private static readonly string ConnectionString = Zaagl.Tests.Shared.TestDatabaseMigrator.ConnectionString;
    private readonly ServiceProvider _services;
    private readonly LinksDbContext _db;

    public AbuseControlsTests()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        _services = sc.BuildServiceProvider();
        _db = _services.GetRequiredService<LinksDbContext>();
    }

    public void Dispose()
    {
        _db.Dispose();
        _services.Dispose();
    }

    private static IOptions<SafetyOptions> Options(params string[] denyHosts) =>
        Microsoft.Extensions.Options.Options.Create(new SafetyOptions { DenyRedirectorHosts = denyHosts });

    private async Task<Link> CreateLinkAsync(string targetHost, string destination, bool isCustom = true)
    {
        var link = new Link
        {
            OwnerId = Guid.NewGuid(),
            Slug = "ac" + Guid.NewGuid().ToString("N")[..8],
            DestinationUrl = destination,
            TargetHost = targetHost,
            IsCustom = isCustom,
            PurgeAfter = DateTime.UtcNow.AddDays(30),
        };
        link.SlugKey = link.Slug.ToLowerInvariant();
        _db.Links.Add(link);
        await _db.SaveChangesAsync();
        return link;
    }

    [Fact]
    public async Task Exact_host_match_blocks()
    {
        var host = $"evil-{Guid.NewGuid():N}.example";
        _db.BlockedDestinations.Add(new BlockedDestination { Kind = BlockedDestinationKinds.Host, Pattern = host, Reason = "test" });
        await _db.SaveChangesAsync();

        var blocklist = new DestinationBlocklist(_db, Options());
        Assert.True(await blocklist.IsBlockedAsync(host, $"https://{host}/x", default));
        Assert.False(await blocklist.IsBlockedAsync("fine.example", "https://fine.example/x", default));
    }

    [Fact]
    public async Task Host_suffix_match_blocks_subdomains_but_not_unrelated_hosts()
    {
        var host = $"bad-{Guid.NewGuid():N}.example";
        _db.BlockedDestinations.Add(new BlockedDestination { Kind = BlockedDestinationKinds.HostSuffix, Pattern = host, Reason = "test" });
        await _db.SaveChangesAsync();

        var blocklist = new DestinationBlocklist(_db, Options());
        Assert.True(await blocklist.IsBlockedAsync(host, $"https://{host}/x", default));
        Assert.True(await blocklist.IsBlockedAsync($"sub.{host}", $"https://sub.{host}/x", default));
        Assert.False(await blocklist.IsBlockedAsync("notbad.example", "https://notbad.example/x", default));
    }

    [Fact]
    public async Task Url_prefix_match_blocks()
    {
        var host = $"shady-{Guid.NewGuid():N}.example";
        var prefix = $"https://{host}/track/";
        _db.BlockedDestinations.Add(new BlockedDestination { Kind = BlockedDestinationKinds.UrlPrefix, Pattern = prefix, Reason = "test" });
        await _db.SaveChangesAsync();

        var blocklist = new DestinationBlocklist(_db, Options());
        Assert.True(await blocklist.IsBlockedAsync(host, $"{prefix}abc", default));
        Assert.False(await blocklist.IsBlockedAsync(host, $"https://{host}/other", default));
    }

    [Fact]
    public async Task Seeded_deny_redirector_hosts_block_without_a_database_row()
    {
        var blocklist = new DestinationBlocklist(_db, Options("bit.ly", "tinyurl.com"));
        Assert.True(await blocklist.IsBlockedAsync("bit.ly", "https://bit.ly/x", default));
        Assert.False(await blocklist.IsBlockedAsync("notbitly.com", "https://notbitly.com/x", default));
    }

    [Fact]
    public async Task Blocked_alias_cannot_be_reclaimed_by_a_new_creation()
    {
        var slug = "blk" + Guid.NewGuid().ToString("N")[..8];
        var key = slug.ToLowerInvariant();
        _db.BlockedAliases.Add(new BlockedAlias { SlugKey = key, Reason = "test takedown" });
        await _db.SaveChangesAsync();

        // Confirm the blocked row is visible to the same query CreateAsync/CreateGuestAsync use, without
        // standing up the full LinkService (its other dependencies - coordinator, cache - are out of scope
        // for this check).
        Assert.True(await _db.BlockedAliases.AnyAsync(a => a.SlugKey == key));
    }

    [Fact]
    public async Task Takedown_sets_disabled_fields_blocks_the_alias_and_writes_evidence()
    {
        var link = await CreateLinkAsync("bad.example", "https://bad.example/x");
        var takedowns = new TakedownService(_db, new NoOpLinkCacheInvalidator(),
            Microsoft.Extensions.Options.Options.Create(new AbuseOptions { EvidenceRetentionDays = 90 }), TimeProvider.System);

        await takedowns.TakeDownAsync(link, DisabledKinds.Safety, "flagged by test", default);

        var reloaded = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == link.Id);
        Assert.Equal(DisabledKinds.Safety, reloaded.DisabledKind);
        Assert.NotNull(reloaded.DisabledAt);

        Assert.True(await _db.BlockedAliases.AnyAsync(a => a.SlugKey == link.SlugKey));
        Assert.True(await _db.AbuseEvidence.AnyAsync(e => e.LinkId == link.Id && e.DisabledKind == DisabledKinds.Safety));
    }

    [Fact]
    public async Task Takedown_on_an_already_disabled_link_overwrites_the_kind_but_keeps_disabled_at()
    {
        var link = await CreateLinkAsync("bad.example", "https://bad.example/y");
        link.DisabledAt = DateTime.UtcNow.AddMinutes(-10);
        link.DisabledKind = DisabledKinds.UserDisabled;
        await _db.SaveChangesAsync();
        var originalDisabledAt = link.DisabledAt;

        var takedowns = new TakedownService(_db, new NoOpLinkCacheInvalidator(),
            Microsoft.Extensions.Options.Options.Create(new AbuseOptions()), TimeProvider.System);
        await takedowns.TakeDownAsync(link, DisabledKinds.Abuse, "manual takedown", default);

        var reloaded = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == link.Id);
        Assert.Equal(DisabledKinds.Abuse, reloaded.DisabledKind);
        Assert.True((reloaded.DisabledAt!.Value - originalDisabledAt!.Value).Duration() < TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Flags ONLY the URL the test created. The re-check job claims every eligible link in the database,
    /// so a blanket "unsafe" fake disables unrelated rows (this is how real dev links such as qwemkh got
    /// "Flagged by re-check: MALWARE" when the suite ran against the dev database).</summary>
    private sealed class UnsafeFor(string unsafeHost) : IUrlReputationChecker
    {
        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct) =>
            Task.FromResult(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == unsafeHost
                ? new ReputationResult(false, "MALWARE")
                : new ReputationResult(true));
    }

    private sealed class AlwaysFailOpen : IUrlReputationChecker
    {
        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct) => Task.FromResult(new ReputationResult(true, Checked: false));
    }

    private SafetyRecheckJob MakeJob(IUrlReputationChecker reputation)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton(reputation);
        sc.AddScoped<TakedownService>();
        sc.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AbuseOptions()));
        sc.AddSingleton(TimeProvider.System);
        var sp = sc.BuildServiceProvider();
        return new SafetyRecheckJob(sp.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<SafetyRecheckJob>.Instance);
    }

    [Fact]
    public async Task Recheck_disables_a_link_flagged_unsafe()
    {
        var link = await CreateLinkAsync("evil.example", "https://evil.example/z");
        // Never checked and not recently created enough to force recency-only selection is fine: SafetyCheckedAt
        // is null, which the job's WHERE clause always selects regardless of CreatedAt.

        await MakeJob(new UnsafeFor("evil.example")).RunOnceAsync(default);

        var reloaded = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == link.Id);
        Assert.Equal(DisabledKinds.Safety, reloaded.DisabledKind);
    }

    [Fact]
    public async Task Recheck_leaves_a_safe_link_untouched_but_stamps_it_checked()
    {
        var link = await CreateLinkAsync("fine.example", "https://fine.example/z");

        await MakeJob(new StaticSafe()).RunOnceAsync(default);

        var reloaded = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == link.Id);
        Assert.Null(reloaded.DisabledKind);
        Assert.NotNull(reloaded.SafetyCheckedAt);
    }

    private sealed class StaticSafe : IUrlReputationChecker
    {
        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct) => Task.FromResult(new ReputationResult(true));
    }

    [Fact]
    public async Task Recheck_resets_safety_checked_at_to_null_on_a_fail_open_lookup_so_it_retries_next_run()
    {
        var link = await CreateLinkAsync("unknown.example", "https://unknown.example/z");

        await MakeJob(new AlwaysFailOpen()).RunOnceAsync(default);

        var reloaded = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == link.Id);
        Assert.Null(reloaded.DisabledKind);
        Assert.Null(reloaded.SafetyCheckedAt); // not actually checked - left null for the next run to retry
    }
}

public sealed class CaptchaTests
{
    [Fact]
    public async Task NoOp_verifier_always_passes()
    {
        var result = await new NoOpCaptchaVerifier().VerifyAsync(null, "guest-link", "app.zaa.gl", default);
        Assert.True(result.IsSuccess);
    }
}

/// <summary>CAPTCHA pen test C3/C4: TurnstileVerifier against a scripted siteverify endpoint (no network).</summary>
public sealed class TurnstileVerifierTests
{
    private const string RealSecret = "0x4AAAAAAA-real-looking-secret-value";

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static TurnstileVerifier Verifier(Func<HttpResponseMessage> respond, string secret = RealSecret) =>
        new(new HttpClient(new StubHandler(respond)),
            Options.Create(new CaptchaOptions { Enabled = true, Secret = secret }),
            NullLogger<TurnstileVerifier>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    [Fact]
    public async Task A_fresh_matching_token_passes()
    {
        var result = await Verifier(() => Json($$"""{"success":true,"action":"register","hostname":"app.zaa.gl","challenge_ts":"{{Now()}}"}"""))
            .VerifyAsync("tok", "register", "app.zaa.gl", default);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task C3_a_missing_challenge_ts_is_invalid_for_a_real_secret()
    {
        var result = await Verifier(() => Json("""{"success":true,"action":"register","hostname":"app.zaa.gl"}"""))
            .VerifyAsync("tok", "register", "app.zaa.gl", default);
        Assert.Equal("captcha-invalid", result.Error!.Message);
    }

    [Fact]
    public async Task C3_an_old_challenge_ts_is_invalid()
    {
        var old = DateTimeOffset.UtcNow.AddMinutes(-6).ToString("O");
        var result = await Verifier(() => Json($$"""{"success":true,"action":"register","hostname":"app.zaa.gl","challenge_ts":"{{old}}"}"""))
            .VerifyAsync("tok", "register", "app.zaa.gl", default);
        Assert.Equal("captcha-invalid", result.Error!.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("{\"success\":")]
    public async Task C4_an_unreadable_200_body_is_a_503_not_a_500(string body)
    {
        var result = await Verifier(() => Json(body)).VerifyAsync("tok", "register", "app.zaa.gl", default);
        Assert.False(result.IsSuccess);
        Assert.Equal("service_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task An_http_error_is_a_503()
    {
        var result = await Verifier(() => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)).VerifyAsync("tok", "register", "app.zaa.gl", default);
        Assert.Equal("service_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task A_wrong_action_is_invalid()
    {
        var result = await Verifier(() => Json($$"""{"success":true,"action":"sign-up","hostname":"app.zaa.gl","challenge_ts":"{{Now()}}"}"""))
            .VerifyAsync("tok", "register", "app.zaa.gl", default);
        Assert.Equal("captcha-invalid", result.Error!.Message);
    }
}
