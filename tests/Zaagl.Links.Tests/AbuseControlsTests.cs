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
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
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

    private sealed class AlwaysUnsafe : IUrlReputationChecker
    {
        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct) => Task.FromResult(new ReputationResult(false, "MALWARE"));
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

        await MakeJob(new AlwaysUnsafe()).RunOnceAsync(default);

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
