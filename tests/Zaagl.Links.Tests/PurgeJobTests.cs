using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zaagl.Modules.Analytics;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Infrastructure;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>
/// Integration tests for PurgeJob (spec §7.8) against the real local dev Postgres (same convention as
/// IdentityServiceTests/AnalyticsPhase4Tests - no Testcontainers set up in this solution yet).
/// </summary>
public sealed class PurgeJobTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
    private readonly ServiceProvider _services;
    private readonly LinksDbContext _db;

    public PurgeJobTests()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        sc.AddDbContext<AnalyticsDbContext>(o => AnalyticsModule.Configure(o, ConnectionString));
        sc.AddScoped<IArchiveWriter, ArchiveWriter>();
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton<IPlanProvider>(new FixedPlanProvider(PlanTier.Free));
        sc.AddSingleton(TimeProvider.System);
        sc.AddSingleton<Microsoft.Extensions.Logging.ILogger<PurgeJob>>(NullLogger<PurgeJob>.Instance);
        _services = sc.BuildServiceProvider();
        _db = _services.GetRequiredService<LinksDbContext>();
    }

    public void Dispose()
    {
        _db.Dispose();
        _services.Dispose();
    }

    private async Task<Link> CreatePurgeableLinkAsync(Guid ownerId, DateTime purgeAfter, bool isCustom = false)
    {
        var link = new Link
        {
            OwnerId = ownerId,
            Slug = "pj" + Guid.NewGuid().ToString("N")[..8],
            SlugKey = "",
            DestinationUrl = "https://example.com/purge-test",
            IsCustom = isCustom,
            CodeLength = isCustom ? null : 5,
            CodeNumber = isCustom ? null : Random.Shared.NextInt64(0, 1_000_000),
            KeyVersion = isCustom ? null : (short)1,
            PurgeAfter = purgeAfter,
            Policy = null,
        };
        link.SlugKey = link.Slug.ToLowerInvariant();
        _db.Links.Add(link);
        await _db.SaveChangesAsync();
        return link;
    }

    private PurgeJob MakeJob(PlanTier tier)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        sc.AddDbContext<AnalyticsDbContext>(o => AnalyticsModule.Configure(o, ConnectionString));
        sc.AddScoped<IArchiveWriter, ArchiveWriter>();
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton<IPlanProvider>(new FixedPlanProvider(tier));
        var sp = sc.BuildServiceProvider();
        return new PurgeJob(sp.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<PurgeJob>.Instance);
    }

    [Fact]
    public async Task Purges_a_link_past_its_purge_after_and_removes_the_row()
    {
        var owner = Guid.NewGuid();
        var link = await CreatePurgeableLinkAsync(owner, DateTime.UtcNow.AddMinutes(-1));

        await MakeJob(PlanTier.Free).RunOnceAsync(default);

        var stillExists = await _db.Links.AnyAsync(l => l.Id == link.Id);
        Assert.False(stillExists);
    }

    [Fact]
    public async Task Does_not_purge_a_link_not_yet_due()
    {
        var owner = Guid.NewGuid();
        var link = await CreatePurgeableLinkAsync(owner, DateTime.UtcNow.AddDays(1));

        await MakeJob(PlanTier.Free).RunOnceAsync(default);

        var stillExists = await _db.Links.AnyAsync(l => l.Id == link.Id);
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Free_plan_owner_gets_no_archive_row()
    {
        var owner = Guid.NewGuid();
        var link = await CreatePurgeableLinkAsync(owner, DateTime.UtcNow.AddMinutes(-1));

        await MakeJob(PlanTier.Free).RunOnceAsync(default);

        var analytics = _services.GetRequiredService<AnalyticsDbContext>();
        var archived = await analytics.LinkArchive.AnyAsync(a => a.LinkId == link.Id);
        Assert.False(archived);
    }

    [Fact]
    public async Task Starter_plan_owner_gets_an_archive_row_with_the_right_code_and_target()
    {
        var owner = Guid.NewGuid();
        var link = await CreatePurgeableLinkAsync(owner, DateTime.UtcNow.AddMinutes(-1));

        await MakeJob(PlanTier.Starter).RunOnceAsync(default);

        var analytics = _services.GetRequiredService<AnalyticsDbContext>();
        var archived = await analytics.LinkArchive.FirstOrDefaultAsync(a => a.LinkId == link.Id);
        Assert.NotNull(archived);
        Assert.Equal(link.Slug, archived!.Code);
        Assert.Equal(link.DestinationUrl, archived.TargetUrl);
        Assert.Equal(owner, archived.OwnerId);
    }

    [Fact]
    public async Task A_link_with_no_purge_after_is_never_purged()
    {
        var owner = Guid.NewGuid();
        var link = new Link
        {
            OwnerId = owner,
            Slug = "pj" + Guid.NewGuid().ToString("N")[..8],
            DestinationUrl = "https://example.com/no-expiry",
            IsCustom = true,
            PurgeAfter = null, // never expires
        };
        link.SlugKey = link.Slug.ToLowerInvariant();
        _db.Links.Add(link);
        await _db.SaveChangesAsync();

        await MakeJob(PlanTier.Free).RunOnceAsync(default);

        Assert.True(await _db.Links.AnyAsync(l => l.Id == link.Id));
    }

    private sealed class FixedPlanProvider(PlanTier tier) : IPlanProvider
    {
        public Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult(PlanLimits.For(tier));
    }
}
