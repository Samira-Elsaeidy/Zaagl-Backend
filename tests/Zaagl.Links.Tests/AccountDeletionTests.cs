using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Analytics;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Infrastructure;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>Spec §7.14, against the real local dev Postgres with the real cross-module services wired
/// up (same convention as PurgeJobTests), not fakes - this is the one place that genuinely exercises
/// IAccountDataEraser/ILinkExportSource/IArchiveQueries together.</summary>
public sealed class AccountDeletionTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
    private readonly ServiceProvider _services;
    private readonly IdentityDbContext _identityDb;
    private readonly LinksDbContext _linksDb;

    public AccountDeletionTests()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<IdentityDbContext>(o => IdentityModule.Configure(o, ConnectionString));
        sc.AddDbContext<LinksDbContext>(o => LinksModule.Configure(o, ConnectionString));
        sc.AddDbContext<AnalyticsDbContext>(o => AnalyticsModule.Configure(o, ConnectionString));
        sc.AddSingleton<ILinkCacheInvalidator, NoOpLinkCacheInvalidator>();
        sc.AddSingleton(TimeProvider.System);
        sc.AddSingleton(Options.Create(new LinkOptions { QuarantineDays = 90 }));
        sc.AddScoped<IArchiveWriter, ArchiveWriter>();
        sc.AddScoped<IArchiveQueries, ArchiveQueries>();
        sc.AddScoped<Zaagl.Modules.Links.Contracts.IAccountDataEraser, AccountDataEraser>();
        sc.AddScoped<Zaagl.Modules.Links.Contracts.ILinkExportSource, LinkExportSource>();
        sc.AddSingleton<IPlanProvider>(new FixedPlanProvider(PlanTier.Free));
        sc.AddSingleton(Options.Create(new JwtOptions { Key = "test-signing-key-at-least-32-characters-long!!" }));
        sc.AddSingleton<TokenService>();
        sc.AddScoped<IdentityService>();
        _services = sc.BuildServiceProvider();
        _identityDb = _services.GetRequiredService<IdentityDbContext>();
        _linksDb = _services.GetRequiredService<LinksDbContext>();
    }

    public void Dispose()
    {
        _identityDb.Dispose();
        _linksDb.Dispose();
        _services.Dispose();
    }

    private sealed class FixedPlanProvider(PlanTier tier) : IPlanProvider
    {
        public Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) => Task.FromResult(PlanLimits.For(tier));
    }

    private async Task<User> CreateUserAsync(string? role = null)
    {
        var user = new User
        {
            Email = $"del-test-{Guid.NewGuid():N}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-password-123"),
            DisplayName = "Test",
            EmailConfirmed = true,
            Role = role,
        };
        _identityDb.Users.Add(user);
        await _identityDb.SaveChangesAsync();
        return user;
    }

    private async Task<Link> CreateLinkAsync(Guid ownerId, DateTime? expiresAt = null)
    {
        var link = new Link
        {
            OwnerId = ownerId,
            Slug = "del" + Guid.NewGuid().ToString("N")[..8],
            DestinationUrl = "https://example.com/account-deletion-test",
            TargetHost = "example.com",
            IsCustom = true,
            PurgeAfter = expiresAt?.AddDays(90) ?? DateTime.UtcNow.AddDays(90),
            Policy = expiresAt is { } e ? new AccessPolicy { ExpiresAt = e } : null,
        };
        link.SlugKey = link.Slug.ToLowerInvariant();
        _linksDb.Links.Add(link);
        await _linksDb.SaveChangesAsync();
        return link;
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_nothing_changes()
    {
        var user = await CreateUserAsync();
        var identity = _services.GetRequiredService<IdentityService>();

        var result = await identity.DeleteAccountAsync(user.Id, "wrong-password", default);

        Assert.False(result.IsSuccess);
        Assert.Null((await _identityDb.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).DeletedAt);
    }

    [Fact]
    public async Task The_last_remaining_admin_cannot_delete_their_own_account()
    {
        var admin = await CreateUserAsync("Admin");
        var identity = _services.GetRequiredService<IdentityService>();

        // This shared dev Postgres always has at least the AdminBootstrap account, so "last remaining
        // admin" can't be reproduced without temporarily clearing every OTHER admin's role - restored in
        // `finally` regardless of outcome, so this never permanently touches that account.
        var otherAdminIds = await _identityDb.Users.Where(u => u.Id != admin.Id && u.Role == "Admin").Select(u => u.Id).ToListAsync();
        await _identityDb.Users.Where(u => otherAdminIds.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, (string?)null));
        try
        {
            var result = await identity.DeleteAccountAsync(admin.Id, "correct-password-123", default);
            Assert.False(result.IsSuccess);
            Assert.Equal("conflict", result.Error!.Code);
        }
        finally
        {
            await _identityDb.Users.Where(u => otherAdminIds.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, "Admin"));
        }
    }

    [Fact]
    public async Task An_admin_can_delete_themself_when_another_admin_exists()
    {
        await CreateUserAsync("Admin"); // the "other" admin
        var admin = await CreateUserAsync("Admin");
        var identity = _services.GetRequiredService<IdentityService>();

        var result = await identity.DeleteAccountAsync(admin.Id, "correct-password-123", default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Deletion_anonymizes_the_profile_revokes_sessions_and_soft_deletes_links()
    {
        var user = await CreateUserAsync();
        var apiKey = new ApiKey { UserId = user.Id, Name = "k", Prefix = "zgl_test", KeyHash = Guid.NewGuid().ToString("N") };
        _identityDb.ApiKeys.Add(apiKey);
        var refresh = new RefreshToken { UserId = user.Id, FamilyId = Guid.CreateVersion7(), TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(30) };
        _identityDb.RefreshTokens.Add(refresh);
        await _identityDb.SaveChangesAsync();

        var link = await CreateLinkAsync(user.Id, DateTime.UtcNow.AddDays(100));

        var identity = _services.GetRequiredService<IdentityService>();
        var result = await identity.DeleteAccountAsync(user.Id, "correct-password-123", default);
        Assert.True(result.IsSuccess);

        var reloadedUser = await _identityDb.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.NotNull(reloadedUser.DeletedAt);
        Assert.Null(reloadedUser.PasswordHash);
        Assert.DoesNotContain("@example.com", reloadedUser.Email);

        Assert.NotNull((await _identityDb.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == apiKey.Id)).RevokedAt);
        Assert.NotNull((await _identityDb.RefreshTokens.AsNoTracking().FirstAsync(t => t.Id == refresh.Id)).RevokedAt);

        var reloadedLink = await _linksDb.Links.Include(l => l.Policy).AsNoTracking().FirstAsync(l => l.Id == link.Id);
        Assert.NotNull(reloadedLink.DeletedAt);
        Assert.True(reloadedLink.Policy!.ExpiresAt <= DateTime.UtcNow); // least(expires_at, now)
        Assert.True(reloadedLink.PurgeAfter <= DateTime.UtcNow.AddDays(90).AddMinutes(1)); // shortened quarantine
    }

    [Fact]
    public async Task A_deleted_account_cannot_sign_in()
    {
        var user = await CreateUserAsync();
        var identity = _services.GetRequiredService<IdentityService>();
        await identity.DeleteAccountAsync(user.Id, "correct-password-123", default);

        // The email changed on delete, so sign-in must use the ORIGINAL address, which no longer resolves
        // to any user - this itself is part of the guarantee (the account is gone under its old identity).
        var outcome = await identity.LoginAsync(new LoginRequest(user.Email, "correct-password-123", null), "test:partition", default);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task Export_returns_the_profile_and_live_links()
    {
        var user = await CreateUserAsync();
        await CreateLinkAsync(user.Id, DateTime.UtcNow.AddDays(30));

        var identity = _services.GetRequiredService<IdentityService>();
        var result = await identity.ExportAsync(user.Id, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Email, result.Value!.Profile.Email);
        Assert.Single(result.Value.Links);
    }
}
