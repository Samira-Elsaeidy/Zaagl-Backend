using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Billing;
using Zaagl.Modules.Billing.Application;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Billing.Domain;
using Zaagl.Modules.Billing.Infrastructure;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;

namespace Zaagl.Links.Tests;

/// <summary>
/// PD-58 (spec §7.4 / §8.2, review B2): API-key revocation after a plan change runs after Billing's commit, a failed
/// revocation stays pending and the next billing event completes it, and keys are revoked whenever the new plan has
/// no API keys. Drives <see cref="BillingService.HandleWebhookAsync"/> with signed Stripe events. Real local Postgres.
/// </summary>
public sealed class PlanChangeRevocationTests : IDisposable
{
    private const string WebhookSecret = "whsec_plan_change_tests";
    private const string StarterPrice = "price_plan_change_tests_starter";

    private static readonly Lazy<bool> Migrated = new(() =>
    {
        var builder = new DbContextOptionsBuilder<BillingDbContext>();
        BillingModule.Configure(builder, RealApiFactory.ConnectionString);
        using var db = new BillingDbContext(builder.Options);
        db.Database.Migrate();
        return true;
    });

    private readonly IdentityDbContext _identityDb;
    private readonly BillingDbContext _billingDb;
    private readonly IdentityService _identity;
    private readonly FlakyListener _listener;
    private readonly BillingService _billing;
    private readonly PlanCatalog _catalog;

    public PlanChangeRevocationTests()
    {
        _ = Migrated.Value;

        var identityBuilder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(identityBuilder, RealApiFactory.ConnectionString);
        _identityDb = new IdentityDbContext(identityBuilder.Options);
        var jwt = Options.Create(new JwtOptions { Key = "test-signing-key-at-least-32-characters-long!!" });
        // Identity's own plan read stays Starter, so a key validates exactly while it is not revoked in the database.
        _identity = new IdentityService(_identityDb, new TokenService(jwt), new StarterPlan(),
            System.Reflection.DispatchProxy.Create<IAccountDataEraser, ApiTestHost.ThrowingProxy>(),
            System.Reflection.DispatchProxy.Create<ILinkExportSource, ApiTestHost.ThrowingProxy>(),
            System.Reflection.DispatchProxy.Create<IArchiveQueries, ApiTestHost.ThrowingProxy>(),
            NullLogger<IdentityService>.Instance);

        var billingBuilder = new DbContextOptionsBuilder<BillingDbContext>();
        BillingModule.Configure(billingBuilder, RealApiFactory.ConnectionString);
        _billingDb = new BillingDbContext(billingBuilder.Options);

        _catalog = new PlanCatalog(new StaticMonitor(PlansFromApiSettings()));
        _listener = new FlakyListener(new ApiKeyPlanChangeListener(_identity));
        var outbox = new PlanChangeOutbox(_billingDb, _catalog, [_listener], TimeProvider.System, NullLogger<PlanChangeOutbox>.Instance);
        var stripe = Options.Create(new StripeOptions { SecretKey = "sk_test_unused", WebhookSecret = WebhookSecret, StarterPriceId = StarterPrice });
        _billing = new BillingService(_billingDb, stripe, _catalog, new MemoryCache(new MemoryCacheOptions()), outbox,
            TimeProvider.System, NullLogger<BillingService>.Instance);
    }

    public void Dispose()
    {
        _billingDb.Dispose();
        _identityDb.Dispose();
    }

    [Fact]
    public async Task A_downgrade_revokes_the_keys_and_leaves_nothing_pending()
    {
        var (userId, secret, sub) = await StarterUserWithKeyAsync(PlanTier.Starter);

        await SendAsync("customer.subscription.deleted", sub, "canceled");

        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
        Assert.Empty(await _identity.ListApiKeysAsync(userId, default));
        Assert.False(await _billingDb.PendingPlanChanges.AnyAsync(p => p.UserId == userId));
        Assert.Equal(PlanTier.Free, await TierAsync(userId));
    }

    [Fact]
    public async Task A_failed_revocation_stays_pending_and_the_next_billing_event_completes_it()
    {
        var (userId, secret, sub) = await StarterUserWithKeyAsync(PlanTier.Starter);
        _listener.FailuresLeft = 1;

        await SendAsync("customer.subscription.deleted", sub, "canceled");

        // The tier change is committed, the revocation failed: the key is still live and a marker records the failure.
        Assert.Equal(PlanTier.Free, await TierAsync(userId));
        Assert.NotNull(await _identity.ValidateApiKeyAsync(secret, default));
        var pending = await _billingDb.PendingPlanChanges.AsNoTracking().SingleAsync(p => p.UserId == userId);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(PlanTier.Starter, pending.PreviousTier);
        Assert.Contains("simulated", pending.LastError);

        // Any later billing event (here: one for an unknown subscription) retries the pending change.
        await SendAsync("customer.subscription.updated", new StripeIds("sub_" + Guid.NewGuid().ToString("N"), "cus_" + Guid.NewGuid().ToString("N")), "active");

        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
        Assert.False(await _billingDb.PendingPlanChanges.AnyAsync(p => p.UserId == userId));

        // Spec §8.2: an upgrade does not restore revoked keys.
        await SendAsync("customer.subscription.updated", sub, "active");
        Assert.Equal(PlanTier.Starter, await TierAsync(userId));
        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
    }

    [Fact]
    public async Task The_retry_job_pass_also_completes_a_pending_revocation()
    {
        var (userId, secret, sub) = await StarterUserWithKeyAsync(PlanTier.Starter);
        _listener.FailuresLeft = 1;
        await SendAsync("customer.subscription.deleted", sub, "canceled");
        Assert.True(await _billingDb.PendingPlanChanges.AnyAsync(p => p.UserId == userId));

        var outbox = new PlanChangeOutbox(_billingDb, _catalog, [_listener], TimeProvider.System, NullLogger<PlanChangeOutbox>.Instance);
        await outbox.ProcessPendingAsync(default);

        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
        Assert.False(await _billingDb.PendingPlanChanges.AnyAsync(p => p.UserId == userId));
    }

    [Fact]
    public async Task A_billing_event_that_keeps_a_plan_without_API_keys_still_revokes()
    {
        // The user is already Free in Billing but still holds a live key (a revocation that was lost before PD-58).
        var (userId, secret, sub) = await StarterUserWithKeyAsync(PlanTier.Free);
        Assert.NotNull(await _identity.ValidateApiKeyAsync(secret, default));

        await SendAsync("customer.subscription.updated", sub, "incomplete_expired");

        Assert.Equal(PlanTier.Free, await TierAsync(userId));
        Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
        Assert.False(await _billingDb.PendingPlanChanges.AnyAsync(p => p.UserId == userId));
    }

    [Fact]
    public async Task The_listener_revokes_for_any_new_plan_without_API_keys_whatever_the_previous_plan()
    {
        var free = _catalog.For(PlanTier.Free).Capabilities;
        var starter = _catalog.For(PlanTier.Starter).Capabilities;
        var paidWithoutKeys = starter with { ApiKeys = false };
        var listener = new ApiKeyPlanChangeListener(_identity);

        foreach (var previous in new[] { starter, free, paidWithoutKeys })
        {
            foreach (var current in new[] { free, paidWithoutKeys })
            {
                var (userId, secret, _) = await StarterUserWithKeyAsync(PlanTier.Starter);
                await listener.OnPlanChangedAsync(userId, previous, current, default);
                Assert.Null(await _identity.ValidateApiKeyAsync(secret, default));
            }
        }

        // A new plan that has API keys leaves them alone.
        var (keeper, keeperSecret, _) = await StarterUserWithKeyAsync(PlanTier.Starter);
        await listener.OnPlanChangedAsync(keeper, free, starter, default);
        Assert.NotNull(await _identity.ValidateApiKeyAsync(keeperSecret, default));
    }

    // ---- helpers -----------------------------------------------------------

    private sealed record StripeIds(string SubscriptionId, string CustomerId);

    private async Task<(Guid UserId, string Secret, StripeIds Sub)> StarterUserWithKeyAsync(PlanTier billingTier)
    {
        var email = $"plan-change-{Guid.NewGuid():N}@example.test";
        await _identity.RegisterAsync(new(email, "password123", null), default);
        var userId = await _identityDb.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var created = await _identity.CreateApiKeyAsync(userId, "ci", null, default);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var ids = new StripeIds("sub_" + Guid.NewGuid().ToString("N"), "cus_" + Guid.NewGuid().ToString("N"));
        _billingDb.Subscriptions.Add(new Subscription
        {
            UserId = userId, Tier = billingTier, StripeSubscriptionId = ids.SubscriptionId, StripeCustomerId = ids.CustomerId,
            Status = billingTier == PlanTier.Starter ? "active" : "canceled", UpdatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
        });
        await _billingDb.SaveChangesAsync();
        _billingDb.ChangeTracker.Clear();
        return (userId, created.Value!.Secret, ids);
    }

    private async Task<PlanTier> TierAsync(Guid userId) =>
        await _billingDb.Subscriptions.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.Tier).SingleAsync();

    private async Task SendAsync(string type, StripeIds sub, string status)
    {
        var payload = SignedEvent(type, sub, status, out var signature);
        var result = await _billing.HandleWebhookAsync(payload, signature, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        _billingDb.ChangeTracker.Clear();
    }

    /// <summary>A Stripe subscription event signed the way Stripe signs webhooks (t=..,v1=HMAC-SHA256).</summary>
    private static string SignedEvent(string type, StripeIds sub, string status, out string signature)
    {
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        var payload = $$"""
            {
              "id": "evt_{{Guid.NewGuid():N}}",
              "object": "event",
              "api_version": "{{Stripe.StripeConfiguration.ApiVersion}}",
              "created": {{now}},
              "livemode": false,
              "pending_webhooks": 0,
              "type": "{{type}}",
              "data": {
                "object": {
                  "id": "{{sub.SubscriptionId}}",
                  "object": "subscription",
                  "customer": "{{sub.CustomerId}}",
                  "status": "{{status}}",
                  "items": {
                    "object": "list",
                    "data": [ { "id": "si_1", "object": "subscription_item", "price": { "id": "{{StarterPrice}}", "object": "price" } } ]
                  }
                }
              }
            }
            """;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{now}.{payload}"));
        signature = $"t={now},v1={Convert.ToHexStringLower(mac)}";
        return payload;
    }

    private static PlansOptions PlansFromApiSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Zaagl.Api", "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build();
        return config.GetSection(PlansOptions.Section).Get<PlansOptions>()!;
    }

    /// <summary>Wraps the real Identity listener; throws while <see cref="FailuresLeft"/> is above zero.</summary>
    private sealed class FlakyListener(IPlanChangeListener inner) : IPlanChangeListener
    {
        public int FailuresLeft { get; set; }

        public Task OnPlanChangedAsync(Guid userId, PlanCapabilities previous, PlanCapabilities current, CancellationToken ct)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new InvalidOperationException("simulated Identity outage");
            }
            return inner.OnPlanChangedAsync(userId, previous, current, ct);
        }
    }

    private sealed class StarterPlan : IContentPlanProvider
    {
        public Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken ct) => Task.FromResult(FixedContentPlanProvider.For(PlanTier.Starter));
        public ContentPlan Guest => new FixedContentPlanProvider(PlanTier.Free).Guest;
    }

    private sealed class StaticMonitor(PlansOptions value) : IOptionsMonitor<PlansOptions>
    {
        public PlansOptions CurrentValue => value;
        public PlansOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PlansOptions, string?> listener) => null;
    }
}
