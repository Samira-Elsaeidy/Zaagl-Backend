using System.Net;
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Billing.Application;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.Modules.Resolution.Application;
using Zaagl.Modules.Resources.Application;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Links.Tests;

/// <summary>BE-003 plan model v2 (ARC-005): configured values, validation and capability reads.</summary>
public sealed class PlanModelTests
{
    private static PlansOptions FromApiSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Zaagl.Api", "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build();
        return config.GetSection(PlansOptions.Section).Get<PlansOptions>()!;
    }

    [Fact]
    public void Shipped_plan_configuration_is_valid_and_has_the_approved_values()
    {
        var options = FromApiSettings();
        Assert.True(new PlansOptionsValidator().Validate(null, options).Succeeded);

        var catalog = new PlanCatalog(new StaticMonitor(options));
        var free = catalog.For(PlanTier.Free);
        var starter = catalog.For(PlanTier.Starter);
        var guest = catalog.Guest;

        // PD-04, PD-54, PD-56: 1 MB = 1,048,576 bytes.
        Assert.Equal(25L * 1_048_576, free.Limits.MaxFileBytes);
        Assert.Equal(100L * 1_048_576, starter.Limits.MaxFileBytes);
        Assert.Equal(25L * 1_048_576, guest.Limits.MaxFileBytes);
        Assert.Equal(250L * 1_048_576, free.Limits.StorageQuotaBytes);
        Assert.Equal(5L * 1024 * 1_048_576, starter.Limits.StorageQuotaBytes);
        Assert.Null(guest.Limits.StorageQuotaBytes);
        Assert.Equal(256L * 1024, guest.Limits.MaxSnippetBytes);
        Assert.Equal(512L * 1024, free.Limits.MaxSnippetBytes);
        Assert.Equal(2L * 1_048_576, starter.Limits.MaxSnippetBytes);
        Assert.Equal((7, 7, 30), (guest.Limits.RetentionDays, free.Limits.RetentionDays, starter.Limits.RetentionDays));

        // PD-54: burn and password on content for Starter only; PD-07: API keys paid only.
        Assert.True(starter.Capabilities.BurnAfterDownload && starter.Capabilities.PasswordOnContent && starter.Capabilities.ApiKeys);
        Assert.False(free.Capabilities.BurnAfterDownload || free.Capabilities.PasswordOnContent || free.Capabilities.ApiKeys);
        Assert.False(guest.Capabilities.BurnAfterDownload || guest.Capabilities.PasswordOnContent || guest.Capabilities.ManageContent);
        Assert.True(guest.IsGuest);
    }

    [Fact]
    public void Validation_fails_on_a_missing_capability_or_an_oversized_file_limit()
    {
        var options = FromApiSettings();
        options.Free!.Capabilities!.ApiKeys = null;
        options.Starter!.Content!.MaxFileBytes = PlansOptions.MaxFileBytesCeiling + 1;
        options.Guest!.Content!.StorageQuotaBytes = 1;

        var result = new PlansOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Plans:Free:Capabilities:ApiKeys"));
        Assert.Contains(result.Failures!, f => f.Contains("Plans:Starter:Content:MaxFileBytes"));
        Assert.Contains(result.Failures!, f => f.Contains("Plans:Guest:Content:StorageQuotaBytes"));
    }

    private sealed class StaticMonitor(PlansOptions value) : IOptionsMonitor<PlansOptions>
    {
        public PlansOptions CurrentValue => value;
        public PlansOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PlansOptions, string?> listener) => null;
    }
}

/// <summary>Spec §7.4 / ARC-005 §4 / ARC-014 §3.2: API keys follow the plan's ApiKeys capability. Real local Postgres.</summary>
public sealed class ApiKeyPlanTests : IDisposable
{
    private readonly IdentityDbContext _db;
    private readonly MutablePlan _plan = new() { Tier = PlanTier.Starter };
    private readonly IdentityService _identity;

    public ApiKeyPlanTests()
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(builder, RealApiFactory.ConnectionString);
        _db = new IdentityDbContext(builder.Options);
        var jwt = Options.Create(new JwtOptions { Key = "test-signing-key-at-least-32-characters-long!!" });
        // Account deletion/export are never called here: placeholders that throw if touched.
        _identity = new IdentityService(_db, new TokenService(jwt), _plan,
            System.Reflection.DispatchProxy.Create<IAccountDataEraser, ApiTestHost.ThrowingProxy>(),
            System.Reflection.DispatchProxy.Create<ILinkExportSource, ApiTestHost.ThrowingProxy>(),
            System.Reflection.DispatchProxy.Create<IArchiveQueries, ApiTestHost.ThrowingProxy>(),
            NullLogger<IdentityService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(Guid UserId, string Secret)> UserWithKeyAsync()
    {
        var email = $"keys-{Guid.NewGuid():N}@example.test";
        await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await _db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var created = await _identity.CreateApiKeyAsync(userId, "ci", null, default);
        Assert.True(created.IsSuccess);
        return (userId, created.Value!.Secret);
    }

    [Fact]
    public async Task Free_plan_cannot_create_a_key()
    {
        _plan.Tier = PlanTier.Free;
        var email = $"keys-{Guid.NewGuid():N}@example.test";
        await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await _db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();

        var result = await _identity.CreateApiKeyAsync(userId, "ci", null, default);

        Assert.Equal(ErrorCodes.PlanRequired, result.Error?.Code);
    }

    [Fact]
    public async Task A_key_stops_authenticating_when_the_plan_loses_ApiKeys()
    {
        var (_, secret) = await UserWithKeyAsync();
        Assert.NotNull(await _identity.ValidateApiKeyAsync(secret, default));

        _plan.Tier = PlanTier.Free;

        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
    }

    [Fact]
    public async Task A_downgrade_revokes_every_key_and_an_upgrade_does_not_restore_them()
    {
        var (userId, secret) = await UserWithKeyAsync();
        var listener = new ApiKeyPlanChangeListener(_identity);
        var starter = FixedContentPlanProvider.For(PlanTier.Starter).Capabilities;
        var free = FixedContentPlanProvider.For(PlanTier.Free).Capabilities;

        await listener.OnPlanChangedAsync(userId, starter, free, default);
        _plan.Tier = PlanTier.Starter; // upgrade again

        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
        Assert.Empty(await _identity.ListApiKeysAsync(userId, default));
    }

    private sealed class MutablePlan : IContentPlanProvider
    {
        public PlanTier Tier { get; set; }
        public Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken ct) => Task.FromResult(FixedContentPlanProvider.For(Tier));
        public ContentPlan Guest => new FixedContentPlanProvider(PlanTier.Free).Guest;
    }
}

/// <summary>ARC-014 §3.1: device-flow (native) tokens are never Interactive or Admin.</summary>
public sealed class NativeClientPolicyTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Device_tokens_are_native_and_web_sessions_are_not()
    {
        Assert.True(NativeClientPolicy.IsNativeToken(Principal(new Claim(NativeClientClaims.Amr, NativeClientClaims.AmrDevice))));
        Assert.True(NativeClientPolicy.IsNativeToken(Principal(new Claim(NativeClientClaims.ClientId, NativeClientIds.Cli))));
        Assert.True(NativeClientPolicy.IsNativeToken(Principal(new Claim(NativeClientClaims.ClientId, NativeClientIds.Desktop))));
        Assert.False(NativeClientPolicy.IsNativeToken(Principal(new Claim("sub", Guid.NewGuid().ToString()), new Claim("sstamp", "x"))));
    }
}

/// <summary>ARC-006 §2: computed resource state, first match wins (addendum §2.4).</summary>
public sealed class ResourceStateTests
{
    [Theory]
    [InlineData(null, ProcessingStatus.Ready, false, false, ResourceState.Purgeable)]
    [InlineData(LinkState.Purgeable, ProcessingStatus.Ready, true, true, ResourceState.Purgeable)]
    [InlineData(LinkState.Active, ProcessingStatus.Ready, false, true, ResourceState.Deleted)]
    [InlineData(LinkState.Deleted, ProcessingStatus.Ready, true, false, ResourceState.Deleted)]
    [InlineData(LinkState.Disabled, ProcessingStatus.Ready, true, false, ResourceState.Disabled)]
    [InlineData(LinkState.Quarantined, ProcessingStatus.Ready, true, false, ResourceState.Burned)]
    [InlineData(LinkState.Quarantined, ProcessingStatus.Scanning, false, false, ResourceState.Expired)]
    [InlineData(LinkState.Active, ProcessingStatus.Pending, false, false, ResourceState.Processing)]
    [InlineData(LinkState.Active, ProcessingStatus.Error, false, false, ResourceState.Processing)]
    [InlineData(LinkState.Active, ProcessingStatus.Rejected, false, false, ResourceState.Rejected)]
    [InlineData(LinkState.Active, ProcessingStatus.Ready, false, false, ResourceState.Ready)]
    public void State_follows_the_addendum_order(LinkState? link, ProcessingStatus status, bool burned, bool deleted, ResourceState expected)
    {
        var at = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ResourceStateCalculator.Compute(link, status, burned ? at : null, deleted ? at : null));
    }

    [Fact]
    public void Resource_kinds_mirror_the_content_link_kinds()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
            Assert.Equal(kind.ToString(), ((LinkTargetKind)(short)kind).ToString());
        Assert.Equal(Enum.GetValues<ResourceKind>().Length, Enum.GetValues<LinkTargetKind>().Length - 1);
    }
}

/// <summary>ARC-002 §7: the short host sends content links to the viewer; closed ones get the status pages.</summary>
public sealed class ContentRedirectTests
{
    private static LinkSnapshot Content(LinkTargetKind kind, DateTime? expiresAt = null, string? password = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "my-notes", null, true, password, expiresAt ?? DateTime.UtcNow.AddDays(1),
            null, null, null, null, null, false, [], DateTime.UtcNow.AddDays(91), TargetKind: kind, ResourceId: Guid.NewGuid());

    private static ResolutionService Service(LinkSnapshot snapshot, Clicks clicks) =>
        new(new OneLink(snapshot), new AccessGate(), new ClientClassifier(), new NullGeoLookup(), clicks,
            Options.Create(new ResolutionOptions()), TimeProvider.System);

    private static readonly VisitorRequest Visitor = new("203.0.113.9", "Mozilla/5.0 (Windows NT 10.0) Chrome/120", null, null);

    [Theory]
    [InlineData(LinkTargetKind.Snippet, "/p/my-notes")]
    [InlineData(LinkTargetKind.File, "/f/my-notes")]
    [InlineData(LinkTargetKind.Media, "/i/my-notes")]
    public async Task Active_content_goes_to_its_viewer_and_GET_records_one_event(LinkTargetKind kind, string path)
    {
        var clicks = new Clicks();
        var result = await Service(Content(kind), clicks).ResolveAsync("my-notes", Visitor, default);

        Assert.Equal(ResolveKind.Content, result.Kind);
        Assert.Equal(path, result.Url);
        Assert.Equal(1, clicks.Count);
    }

    [Fact]
    public async Task Password_content_still_goes_to_the_viewer_and_HEAD_records_nothing()
    {
        var clicks = new Clicks();
        var result = await Service(Content(LinkTargetKind.File, password: "$2a$hash"), clicks)
            .ResolveAsync("my-notes", Visitor, default, countsAsClick: false);

        Assert.Equal(ResolveKind.Content, result.Kind);
        Assert.Equal(0, clicks.Count);
    }

    [Fact]
    public async Task Expired_content_gets_the_410_page_without_a_fallback_and_the_URL_unlock_refuses_it()
    {
        var service = Service(Content(LinkTargetKind.Snippet, expiresAt: DateTime.UtcNow.AddMinutes(-1)), new Clicks());

        var result = await service.ResolveAsync("my-notes", Visitor, default);
        var unlock = await service.UnlockAsync("my-notes", "pw", Visitor, default);

        Assert.Equal(ResolveKind.Expired, result.Kind);
        Assert.Null(result.Url);
        Assert.Equal(ErrorCodes.NotFound, unlock.Error?.Code);
    }

    private sealed class OneLink(LinkSnapshot snapshot) : ILinkResolver
    {
        public Task<LinkSnapshot?> GetSnapshotAsync(string slugKey, CancellationToken ct) => Task.FromResult<LinkSnapshot?>(snapshot);
        public Task<bool> TryConsumeClickAsync(Guid linkId, CancellationToken ct) => throw new InvalidOperationException("content never consumes clicks");
        public Task IncrementGuestClickCountAsync(Guid linkId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Clicks : IClickRecorder
    {
        public int Count;
        public bool TryRecord(ClickEvent click) { Count++; return true; }
    }
}

/// <summary>ARC-002 §5 and §6: the content-link issuer on the Phase 0 engine, and the URL write routes refusing content
/// links. Custom aliases only (no coordinator lease needed). Real local Postgres with the BE-007 migrations.</summary>
public sealed class ContentLinkIssuerTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    public ContentLinkIssuerTests()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, RealApiFactory.ConnectionString));
        sc.AddHybridCache();
        sc.AddMemoryCache();
        sc.AddSingleton<IPlanProvider>(new ThrowingPlans());
        sc.AddSingleton<IUrlReputationChecker>(new ThrowingReputation());
        sc.AddSingleton<IDestinationBlocklist>(new ThrowingBlocklist());
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton(Options.Create(new LinkOptions { BaseUrl = "https://zaa.test" }));
        sc.AddSingleton(Options.Create(new GuestOptions()));
        sc.AddSingleton(TimeProvider.System);
        sc.AddSingleton(new CodeGenerator(new FeistelPermutation("content-link-issuer-tests")));
        sc.AddSingleton(OpenCreationGate());
        sc.AddPlaceholder(typeof(CoordinatorLeaseClient)); // custom aliases never lease
        sc.AddScoped<IAliasDenylist, AliasDenylist>();
        // The shared dev database may hold an operator freeze left by other tests (test isolation is deferred,
        // QA-001..003): the freeze checker alone reads a clock from before any freeze row, so this suite tests the
        // issuer and not that leftover. The freeze rule itself is covered by the Phase 0 abuse-control tests.
        sc.AddScoped(sp => new AliasFreezeChecker(sp.GetRequiredService<LinksDbContext>(),
            sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(), new FixedClock(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        sc.AddSingleton<IValidator<LinkInput>>(sp => new LinkInputValidator(sp.GetRequiredService<CodeGenerator>()));
        sc.AddScoped<LinkService>();
        _services = sc.BuildServiceProvider();
        _scope = _services.CreateScope();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private LinkService Links => _scope.ServiceProvider.GetRequiredService<LinkService>();
    private LinksDbContext Db => _scope.ServiceProvider.GetRequiredService<LinksDbContext>();

    private static CreationGate OpenCreationGate()
    {
        var http = new HttpClient(new StatusHandler()) { BaseAddress = new Uri("http://coordinator.test") };
        var gate = new CreationGate(http, "fp");
        gate.PollOnceAsync(default).GetAwaiter().GetResult();
        Assert.True(gate.CanCreate);
        return gate;
    }

    private static ContentLinkRequest Request(Guid? owner = null, bool key = true, string? password = null) => new(
        Guid.NewGuid(), LinkTargetKind.File, owner ?? Guid.NewGuid(), null, DateTime.UtcNow.AddDays(7),
        CustomSlug: "c-" + Guid.NewGuid().ToString("N")[..10], Title: "Report", Password: password, RequireLinkKey: key);

    [Fact]
    public async Task A_content_link_has_no_destination_a_key_hash_and_the_account_purge_rule()
    {
        var request = Request(password: "secret-pw");
        var issued = await Links.CreateContentLinkAsync(request, default);

        Assert.True(issued.IsSuccess, issued.Error?.Message);
        var v = issued.Value!;
        Assert.Equal($"https://zaa.test/{request.CustomSlug}", v.ShortUrl);
        Assert.Equal(22, v.LinkKey!.Length);
        Assert.Equal(request.ExpiresAt.AddDays(90), v.PurgeAfter, TimeSpan.FromSeconds(1));

        var row = await Db.Links.AsNoTracking().Include(l => l.Policy).SingleAsync(l => l.Id == v.LinkId);
        Assert.Equal(LinkTargetKind.File, row.TargetKind);
        Assert.Equal(request.ResourceId, row.ResourceId);
        Assert.Null(row.DestinationUrl);
        Assert.Null(row.TargetHost);
        Assert.False(row.PublicStats);
        Assert.Null(row.Policy!.MaxClicks);
        Assert.Null(row.Policy.FallbackUrl);
        Assert.NotNull(row.Policy.PasswordHash);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Buffers.Text.Base64Url.DecodeFromChars(v.LinkKey)), row.Policy.LinkKeyHash);
    }

    [Fact]
    public async Task A_replay_returns_the_same_link_without_the_key_and_a_guest_link_purges_at_expiry()
    {
        var request = Request(key: true);
        var first = await Links.CreateContentLinkAsync(request, default);
        var replay = await Links.CreateContentLinkAsync(request, default);
        Assert.Equal(first.Value!.LinkId, replay.Value!.LinkId);
        Assert.Null(replay.Value.LinkKey);

        var guest = Request() with { OwnerId = null, GuestCreatorKey = Guid.NewGuid().ToByteArray(), RequireLinkKey = false };
        var issued = await Links.CreateContentLinkAsync(guest, default);
        Assert.True(issued.IsSuccess, issued.Error?.Message);
        Assert.Null(issued.Value!.LinkKey);
        Assert.Equal(issued.Value.ExpiresAt, issued.Value.PurgeAfter);
    }

    [Fact]
    public async Task The_URL_write_routes_refuse_a_content_link_and_the_list_hides_it()
    {
        var owner = Guid.NewGuid();
        var issued = (await Links.CreateContentLinkAsync(Request(owner), default)).Value!;

        Assert.Equal(ErrorCodes.ContentLink, (await Links.UpdateAsync(owner, issued.LinkId, new LinkInput("https://example.com/x"), default)).Error?.Code);
        Assert.Equal(ErrorCodes.ContentLink, (await Links.RenewAsync(owner, issued.LinkId, null, default)).Error?.Code);
        Assert.Equal(ErrorCodes.ContentLink, (await Links.SetFlagsAsync(owner, issued.LinkId, new LinkFlagsInput(IsActive: false), default)).Error?.Code);
        Assert.Equal(ErrorCodes.ContentLink, (await Links.DeleteAsync(owner, issued.LinkId, default)).Error?.Code);
        Assert.Empty((await Links.ListAsync(owner, new ListLinksQuery(), default)).Items);

        var row = await Db.Links.AsNoTracking().SingleAsync(l => l.Id == issued.LinkId);
        Assert.Null(row.DeletedAt);
        Assert.Null(row.DisabledAt);
        Assert.Null(row.DestinationUrl);
    }

    [Fact]
    public async Task The_database_rejects_a_content_link_with_a_destination()
    {
        var link = new Link
        {
            OwnerId = Guid.NewGuid(), Slug = "ck-" + Guid.NewGuid().ToString("N")[..10], IsCustom = true,
            TargetKind = LinkTargetKind.Snippet, ResourceId = Guid.NewGuid(),
            DestinationUrl = "https://example.com/", TargetHost = "example.com",
        };
        link.SlugKey = link.Slug;
        Db.Links.Add(link);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());
        Assert.Contains("ck_links_target_destination", ex.InnerException!.Message);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"keyFingerprint":"fp","leasing":"ok"}""", Encoding.UTF8, "application/json"),
            });
    }

    // ARC-002 §5.4: the issuer never runs the URL-only steps; these fail the test if it does.
    private sealed class ThrowingReputation : IUrlReputationChecker
    {
        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct) => throw new InvalidOperationException("Web Risk called");
    }

    private sealed class ThrowingBlocklist : IDestinationBlocklist
    {
        public Task<bool> IsBlockedAsync(string host, string url, CancellationToken ct) => throw new InvalidOperationException("blocklist called");
    }

    private sealed class ThrowingPlans : IPlanProvider
    {
        public Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) => throw new InvalidOperationException("plan read");
    }
}
