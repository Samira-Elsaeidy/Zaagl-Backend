using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

/// <summary>
/// PD-44: a fallback URL gets the destination's checks when set or changed: UrlSafety normalisation, the
/// destination blocklist inside the transaction, and the reputation lookup with the same fail-open rule. Create
/// and update share PrepareAsync and the in-transaction blocklist helper; these tests drive them through
/// UpdateAsync, which needs no coordinator lease. Real local dev Postgres.
/// </summary>
public sealed class FallbackUrlChecksTests : IDisposable
{
    private const string ConnectionString = RealApiFactory.ConnectionString;
    private const string Destination = "https://dest-ok.example/page";

    private sealed class Reputation : IUrlReputationChecker
    {
        public string? UnsafeUrl;
        public bool FailOpen;
        public readonly List<string> Checked = [];

        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct)
        {
            Checked.Add(url);
            if (url == UnsafeUrl) return Task.FromResult(new ReputationResult(false, "MALWARE"));
            return Task.FromResult(FailOpen ? new ReputationResult(true, Checked: false) : new ReputationResult(true));
        }
    }

    private sealed class StarterPlan : IPlanProvider
    {
        public Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) => Task.FromResult(PlanLimits.For(PlanTier.Starter));
    }

    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly LinksDbContext _db;
    private readonly Reputation _reputation = new();

    public FallbackUrlChecksTests()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        sc.AddHybridCache();
        sc.AddSingleton<IUrlReputationChecker>(_reputation);
        sc.AddSingleton<IPlanProvider, StarterPlan>();
        sc.AddSingleton(Options.Create(new SafetyOptions()));
        sc.AddScoped<IDestinationBlocklist, DestinationBlocklist>();
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton(Options.Create(new LinkOptions { BaseUrl = "https://zaa.test" }));
        sc.AddSingleton(Options.Create(new GuestOptions()));
        sc.AddSingleton(TimeProvider.System);
        // Not used by UpdateAsync (no custom slug, no new code): placeholders that fail if touched.
        foreach (var type in new[] { typeof(CoordinatorLeaseClient), typeof(CodeGenerator), typeof(CreationGate), typeof(IAliasDenylist), typeof(AliasFreezeChecker) })
            sc.AddPlaceholder(type);
        sc.AddSingleton<IValidator<LinkInput>>(sp => new LinkInputValidator(sp.GetRequiredService<CodeGenerator>()));
        sc.AddScoped<LinkService>();
        _services = sc.BuildServiceProvider();
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<LinksDbContext>();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private LinkService Service => _scope.ServiceProvider.GetRequiredService<LinkService>();

    private async Task<Link> CreateLinkAsync()
    {
        var link = new Link
        {
            OwnerId = Guid.NewGuid(),
            Slug = "fb" + Guid.NewGuid().ToString("N")[..8],
            DestinationUrl = Destination,
            TargetHost = "dest-ok.example",
            IsCustom = true,
            PurgeAfter = DateTime.UtcNow.AddDays(30),
        };
        link.SlugKey = link.Slug.ToLowerInvariant();
        _db.Links.Add(link);
        await _db.SaveChangesAsync();
        return link;
    }

    private Task<Zaagl.SharedKernel.Result<LinkDto>> SetFallbackAsync(Link link, string fallback) =>
        Service.UpdateAsync(link.OwnerId!.Value, link.Id, new LinkInput(Destination, Policy: new PolicyInput(FallbackUrl: fallback)), default);

    private async Task<string?> StoredFallbackAsync(Link link) =>
        await _db.Set<AccessPolicy>().AsNoTracking().Where(p => p.LinkId == link.Id).Select(p => p.FallbackUrl).FirstOrDefaultAsync();

    [Fact]
    public async Task A_safe_fallback_is_normalised_reputation_checked_and_saved()
    {
        var link = await CreateLinkAsync();
        var result = await SetFallbackAsync(link, "HTTPS://Fallback-OK.example/x");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("https://fallback-ok.example/x", await StoredFallbackAsync(link));
        Assert.Contains("https://fallback-ok.example/x", _reputation.Checked);
    }

    [Fact]
    public async Task A_fallback_flagged_by_the_reputation_lookup_is_refused()
    {
        var link = await CreateLinkAsync();
        _reputation.UnsafeUrl = "https://evil-fallback.example/";

        var result = await SetFallbackAsync(link, "https://evil-fallback.example/");

        Assert.False(result.IsSuccess);
        Assert.StartsWith("fallback-unsafe", result.Error!.Message);
        Assert.Null(await StoredFallbackAsync(link));
    }

    [Fact]
    public async Task A_fallback_on_the_blocklist_is_refused_inside_the_transaction()
    {
        var host = $"blocked-fb-{Guid.NewGuid():N}.example";
        _db.BlockedDestinations.Add(new BlockedDestination { Kind = BlockedDestinationKinds.Host, Pattern = host, Reason = "test" });
        await _db.SaveChangesAsync();
        var link = await CreateLinkAsync();

        var result = await SetFallbackAsync(link, $"https://{host}/x");

        Assert.False(result.IsSuccess);
        Assert.StartsWith("fallback-blocked", result.Error!.Message);
        Assert.Null(await StoredFallbackAsync(link));
    }

    [Fact]
    public async Task A_fail_open_lookup_on_the_fallback_saves_it_but_leaves_the_link_unchecked()
    {
        var link = await CreateLinkAsync();
        _reputation.FailOpen = true;

        var result = await SetFallbackAsync(link, "https://fallback-ok.example/y");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("https://fallback-ok.example/y", await StoredFallbackAsync(link));
        // The service updated this tracked entity (same scope). Not re-read from the shared database: the
        // safety re-check job in AbuseControlsTests may stamp unchecked rows concurrently.
        Assert.Null(link.SafetyCheckedAt);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://zaa.test/abcde")]
    public async Task A_fallback_that_fails_url_safety_is_refused(string fallback)
    {
        var link = await CreateLinkAsync();
        var result = await SetFallbackAsync(link, fallback);

        Assert.False(result.IsSuccess);
        Assert.StartsWith("Fallback URL:", result.Error!.Message);
    }
}
