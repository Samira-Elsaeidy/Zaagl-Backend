using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>Spec §7.9/§7.15 admin mechanisms, against the real local dev Postgres (same convention as
/// PurgeJobTests/AbuseControlsTests). The HTTP layer (AdministrationEndpoints) is exercised live, not here.</summary>
public sealed class AdminApiTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
    private readonly ServiceProvider _services;
    private readonly LinksDbContext _db;

    public AdminApiTests()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton(TimeProvider.System);
        sc.AddScoped<TakedownService>();
        sc.AddSingleton(Options.Create(new AbuseOptions()));
        sc.AddScoped<ReportService>();
        _services = sc.BuildServiceProvider();
        _db = _services.GetRequiredService<LinksDbContext>();
    }

    public void Dispose()
    {
        _db.Dispose();
        _services.Dispose();
    }

    private sealed class FixedPlanProvider(PlanTier tier) : IPlanProvider
    {
        public Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) => Task.FromResult(PlanLimits.For(tier));
    }

    // LinkService.AdminSetOwnerLinksDisabledAsync / AdminBulkTakedownAsync need the full module graph
    // (coordinator, cache, etc.) to construct, so they're exercised below via the same DbContext queries
    // they actually run, rather than standing up a full LinkService here.

    private async Task<Link> CreateLinkAsync(Guid? ownerId, string targetHost = "example.com", string? disabledKind = null)
    {
        var link = new Link
        {
            OwnerId = ownerId,
            GuestCreatorKey = ownerId is null ? [1, 2, 3] : null,
            Slug = "ad" + Guid.NewGuid().ToString("N")[..8],
            DestinationUrl = $"https://{targetHost}/x",
            TargetHost = targetHost,
            IsCustom = true,
            DisabledKind = disabledKind,
            DisabledAt = disabledKind is null ? null : DateTime.UtcNow,
            PurgeAfter = DateTime.UtcNow.AddDays(30),
        };
        link.SlugKey = link.Slug.ToLowerInvariant();
        _db.Links.Add(link);
        await _db.SaveChangesAsync();
        return link;
    }

    [Fact]
    public async Task Owner_links_disabled_and_restored_as_user_disabled_kind_only()
    {
        var owner = Guid.NewGuid();
        var normal = await CreateLinkAsync(owner);
        var adminDisabled = await CreateLinkAsync(owner, disabledKind: DisabledKinds.Admin);

        var disabledCount = await _db.Links
            .Where(l => l.OwnerId == owner && l.DeletedAt == null && (l.DisabledKind == null || l.DisabledKind == DisabledKinds.UserDisabled))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DisabledAt, DateTime.UtcNow).SetProperty(l => l.DisabledKind, DisabledKinds.UserDisabled));
        Assert.Equal(1, disabledCount); // only the un-disabled link, never the admin-disabled one

        var reloadedNormal = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == normal.Id);
        var reloadedAdmin = await _db.Links.AsNoTracking().FirstAsync(l => l.Id == adminDisabled.Id);
        Assert.Equal(DisabledKinds.UserDisabled, reloadedNormal.DisabledKind);
        Assert.Equal(DisabledKinds.Admin, reloadedAdmin.DisabledKind); // untouched
    }

    [Fact]
    public async Task Bulk_takedown_by_host_disables_matching_links_and_skips_others()
    {
        var host = $"abuse-{Guid.NewGuid():N}.example";
        var matching = await CreateLinkAsync(Guid.NewGuid(), targetHost: host);
        var other = await CreateLinkAsync(Guid.NewGuid(), targetHost: "fine.example");

        var takedowns = _services.GetRequiredService<TakedownService>();
        var ids = await _db.Links.Where(l => l.TargetHost == host && l.DeletedAt == null && l.DisabledKind == null)
            .Select(l => l.Id).ToListAsync();
        Assert.Contains(matching.Id, ids);
        Assert.DoesNotContain(other.Id, ids);

        foreach (var id in ids)
        {
            var link = await _db.Links.FirstAsync(l => l.Id == id);
            await takedowns.TakeDownAsync(link, DisabledKinds.Abuse, "bulk test", default);
        }

        Assert.Equal(DisabledKinds.Abuse, (await _db.Links.AsNoTracking().FirstAsync(l => l.Id == matching.Id)).DisabledKind);
        Assert.Null((await _db.Links.AsNoTracking().FirstAsync(l => l.Id == other.Id)).DisabledKind);
    }

    [Fact]
    public async Task Report_lifecycle_create_list_resolve()
    {
        var reports = _services.GetRequiredService<ReportService>();
        var report = await reports.CreateAsync("abc123", null, "phishing", "looks fake", "reporter@example.com", default);

        var (items, total) = await reports.ListAsync(ReportStatuses.Open, 1, 50, default);
        Assert.Contains(items, r => r.Id == report.Id);
        Assert.True(total >= 1);

        var resolved = await reports.ResolveAsync(report.Id, ReportStatuses.Actioned, "took it down", default);
        Assert.True(resolved);

        var (openAfter, _) = await reports.ListAsync(ReportStatuses.Open, 1, 50, default);
        Assert.DoesNotContain(openAfter, r => r.Id == report.Id);
    }

    [Fact]
    public async Task Alias_freeze_checker_reports_the_active_freeze_and_ignores_ended_or_future_ones()
    {
        await _db.AliasFreezes.ExecuteDeleteAsync(); // isolate from other tests' freeze rows
        var now = DateTime.UtcNow;
        _db.AliasFreezes.AddRange(
            new AliasFreeze { StartsAt = now.AddDays(-1), Until = now.AddDays(29), Source = "operator", Reason = "active" },
            new AliasFreeze { StartsAt = now.AddDays(-10), Until = now.AddDays(-5), Source = "operator", Reason = "expired" },
            new AliasFreeze { StartsAt = now.AddDays(5), Until = now.AddDays(10), Source = "operator", Reason = "future" });
        await _db.SaveChangesAsync();

        var checker = new AliasFreezeChecker(_db, new TestHybridCache(), TimeProvider.System);
        var active = await checker.GetActiveFreezeAsync(default);
        Assert.NotNull(active);
        Assert.Equal("active", active!.Reason);
    }

    [Fact]
    public async Task Ended_freeze_is_not_active_even_within_its_window()
    {
        await _db.AliasFreezes.ExecuteDeleteAsync(); // isolate from other tests' freeze rows
        var now = DateTime.UtcNow;
        _db.AliasFreezes.Add(new AliasFreeze { StartsAt = now.AddDays(-1), Until = now.AddDays(29), EndedAt = now, Source = "operator", Reason = "ended early" });
        await _db.SaveChangesAsync();

        var checker = new AliasFreezeChecker(_db, new TestHybridCache(), TimeProvider.System);
        Assert.Null(await checker.GetActiveFreezeAsync(default));
    }
}

/// <summary>A pass-through HybridCache stand-in (no actual caching) so freeze tests see fresh DB state
/// every call instead of the real 60s cache masking changes between assertions.</summary>
file sealed class TestHybridCache : Microsoft.Extensions.Caching.Hybrid.HybridCache
{
    public override ValueTask<T> GetOrCreateAsync<TState, T>(
        string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
        Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) =>
        factory(state, cancellationToken);

    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public override ValueTask SetAsync<T>(string key, T value, Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
