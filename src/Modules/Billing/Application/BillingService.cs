using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Stripe;
using Stripe.Checkout;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Billing.Domain;
using Zaagl.Modules.Billing.Infrastructure;
using Zaagl.SharedKernel;
using PlanTier = Zaagl.Modules.Billing.Contracts.PlanTier; // Stripe.net also defines PlanTier

namespace Zaagl.Modules.Billing.Application;

public sealed class StripeOptions
{
    public const string Section = "Stripe";

    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string StarterPriceId { get; set; } = "";
    /// <summary>Base URL of the web app, used for checkout success / cancel and portal return.</summary>
    public string ReturnBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>ARC-005 §2: Stripe price id -> tier. <see cref="StarterPriceId"/> is always mapped to Starter; this
    /// map only adds further prices (none in Phase 1: no Pro, PD-04). An unknown price is logged and ignored.</summary>
    public Dictionary<string, PlanTier> PriceTiers { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(StarterPriceId);

    public PlanTier? TierForPrice(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        if (priceId == StarterPriceId) return PlanTier.Starter;
        return PriceTiers.TryGetValue(priceId, out var tier) ? tier : null;
    }
}

public sealed class PlanProvider(BillingDbContext db) : IPlanProvider
{
    public async Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tier = await db.Subscriptions.Where(s => s.UserId == userId)
            .Select(s => (PlanTier?)s.Tier)
            .FirstOrDefaultAsync(cancellationToken);
        return PlanLimits.For(tier ?? PlanTier.Free);
    }
}

public sealed class BillingService(
    BillingDbContext db,
    IOptions<StripeOptions> options,
    PlanCatalog plans,
    Microsoft.Extensions.Caching.Memory.IMemoryCache planCache,
    PlanChangeOutbox planChanges,
    TimeProvider time,
    ILogger<BillingService> logger)
{
    private readonly StripeOptions _options = options.Value;

    public async Task<BillingStatusDto> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var tier = sub?.Tier ?? PlanTier.Free;
        return new(tier, PlanLimits.For(tier), sub?.StripeCustomerId is not null, plans.For(tier).Capabilities);
    }

    public async Task<Result<RedirectUrlDto>> CreateCheckoutAsync(Guid userId, string email, CancellationToken ct)
    {
        if (!_options.IsConfigured) return Error.Validation("Billing is not configured.");

        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub?.Tier == PlanTier.Starter) return Error.Conflict("You are already on the Starter plan.");

        var client = new StripeClient(_options.SecretKey);
        var create = new SessionCreateOptions
        {
            Mode = "subscription",
            ClientReferenceId = userId.ToString(),
            LineItems = [new SessionLineItemOptions { Price = _options.StarterPriceId, Quantity = 1 }],
            SuccessUrl = $"{_options.ReturnBaseUrl}/settings?checkout=success",
            CancelUrl = $"{_options.ReturnBaseUrl}/settings?checkout=cancelled",
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["user_id"] = userId.ToString() },
            },
        };
        if (sub?.StripeCustomerId is { } customer) create.Customer = customer;
        else create.CustomerEmail = email;

        var session = await new SessionService(client).CreateAsync(create, cancellationToken: ct);
        return Result<RedirectUrlDto>.Ok(new(session.Url));
    }

    public async Task<Result<RedirectUrlDto>> CreatePortalAsync(Guid userId, CancellationToken ct)
    {
        if (!_options.IsConfigured) return Error.Validation("Billing is not configured.");

        var customer = await db.Subscriptions.Where(s => s.UserId == userId)
            .Select(s => s.StripeCustomerId).FirstOrDefaultAsync(ct);
        if (customer is null) return Error.NotFound("No billing account yet.");

        var client = new StripeClient(_options.SecretKey);
        var session = await new Stripe.BillingPortal.SessionService(client).CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customer,
                ReturnUrl = $"{_options.ReturnBaseUrl}/settings",
            }, cancellationToken: ct);
        return Result<RedirectUrlDto>.Ok(new(session.Url));
    }

    public async Task<Result<bool>> HandleWebhookAsync(string payload, string? signature, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret)) return Error.Validation("Webhook is not configured.");

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, _options.WebhookSecret);
        }
        catch (StripeException)
        {
            return Error.Validation("Invalid webhook signature.");
        }

        var startedAt = time.GetUtcNow().UtcDateTime;

        // Idempotency: a unique insert of the event id decides who processes it.
        db.ProcessedEvents.Add(new ProcessedStripeEvent { EventId = stripeEvent.Id });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<bool>.Ok(true);
        }

        switch (stripeEvent.Data.Object)
        {
            case Session session when stripeEvent.Type == "checkout.session.completed":
                await OnCheckoutCompletedAsync(session, ct);
                break;
            case Stripe.Subscription subscription
                when stripeEvent.Type is "customer.subscription.updated" or "customer.subscription.deleted":
                await OnSubscriptionChangedAsync(subscription, stripeEvent.Type == "customer.subscription.deleted", ct);
                break;
            default:
                logger.LogDebug("Ignoring Stripe event {Type}", stripeEvent.Type);
                break;
        }

        // PD-58: every processed billing event also retries plan changes whose delivery failed before.
        try
        {
            await planChanges.ProcessPendingAsync(ct, notAttemptedSince: startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retrying pending plan changes failed; the retry job will try again");
        }
        return Result<bool>.Ok(true);
    }

    private async Task OnCheckoutCompletedAsync(Session session, CancellationToken ct)
    {
        if (!Guid.TryParse(session.ClientReferenceId, out var userId))
        {
            logger.LogWarning("Checkout session {Id} has no valid client reference", session.Id);
            return;
        }

        // Checkout sessions are created only with StarterPriceId (CreateCheckoutAsync), so the price -> tier map
        // resolves that price; a missing mapping means the configuration is broken: log and ignore.
        if (_options.TierForPrice(_options.StarterPriceId) is not { } newTier)
        {
            logger.LogWarning("Checkout session {Id}: no tier is mapped to the configured price; ignored", session.Id);
            return;
        }

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub is null) db.Subscriptions.Add(sub = new Domain.Subscription { UserId = userId });
        var previousTier = sub.Tier;
        sub.Tier = newTier;
        sub.StripeCustomerId = session.CustomerId;
        sub.StripeSubscriptionId = session.SubscriptionId;
        sub.Status = "active";
        sub.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await SaveTierChangeAsync(userId, previousTier, newTier, ct);
    }

    private async Task OnSubscriptionChangedAsync(Stripe.Subscription subscription, bool deleted, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(
            s => s.StripeSubscriptionId == subscription.Id || s.StripeCustomerId == subscription.CustomerId, ct);
        if (sub is null) return;

        var entitled = !deleted && subscription.Status is "active" or "trialing" or "past_due";
        PlanTier newTier;
        if (entitled)
        {
            var priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
            if (_options.TierForPrice(priceId) is not { } mapped)
            {
                logger.LogWarning("Subscription {Id}: price {Price} is not mapped to a tier; event ignored", subscription.Id, priceId);
                return;
            }
            newTier = mapped;
        }
        else
        {
            newTier = PlanTier.Free;
        }

        var previousTier = sub.Tier;
        sub.Tier = newTier;
        sub.Status = deleted ? "canceled" : subscription.Status;
        sub.StripeSubscriptionId = subscription.Id;
        sub.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await SaveTierChangeAsync(sub.UserId, previousTier, newTier, ct);
    }

    /// <summary>Commits the subscription change together with a pending plan-change marker (PD-58: same Billing
    /// transaction, so the listener call cannot be lost), then drops the cached tier and delivers the change to the
    /// listeners (Identity revokes API keys whenever the new plan has none, spec §7.4). Delivery runs after the
    /// commit; a failure is logged and the marker stays for the next billing event or the retry job. Key
    /// authentication re-checks the plan anyway.</summary>
    private async Task SaveTierChangeAsync(Guid userId, PlanTier previousTier, PlanTier newTier, CancellationToken ct)
    {
        var notify = planChanges.NeedsDelivery(previousTier, newTier);
        if (notify)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await planChanges.EnqueueAsync(userId, previousTier, ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        ContentPlanProvider.Invalidate(planCache, userId);
        if (!notify) return;
        try
        {
            await planChanges.DeliverAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The marker is committed; the next billing event or the retry job delivers it.
            logger.LogError(ex, "Delivering the plan change of user {UserId} failed; it stays pending", userId);
        }
    }
}
