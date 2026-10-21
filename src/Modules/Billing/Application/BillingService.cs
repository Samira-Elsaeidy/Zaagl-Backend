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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(StarterPriceId);
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
    ILogger<BillingService> logger)
{
    private readonly StripeOptions _options = options.Value;

    public async Task<BillingStatusDto> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var tier = sub?.Tier ?? PlanTier.Free;
        return new(tier, PlanLimits.For(tier), sub?.StripeCustomerId is not null);
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
        return Result<bool>.Ok(true);
    }

    private async Task OnCheckoutCompletedAsync(Session session, CancellationToken ct)
    {
        if (!Guid.TryParse(session.ClientReferenceId, out var userId))
        {
            logger.LogWarning("Checkout session {Id} has no valid client reference", session.Id);
            return;
        }

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub is null) db.Subscriptions.Add(sub = new Domain.Subscription { UserId = userId });
        sub.Tier = PlanTier.Starter;
        sub.StripeCustomerId = session.CustomerId;
        sub.StripeSubscriptionId = session.SubscriptionId;
        sub.Status = "active";
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task OnSubscriptionChangedAsync(Stripe.Subscription subscription, bool deleted, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(
            s => s.StripeSubscriptionId == subscription.Id || s.StripeCustomerId == subscription.CustomerId, ct);
        if (sub is null) return;

        var entitled = !deleted && subscription.Status is "active" or "trialing" or "past_due";
        sub.Tier = entitled ? PlanTier.Starter : PlanTier.Free;
        sub.Status = deleted ? "canceled" : subscription.Status;
        sub.StripeSubscriptionId = subscription.Id;
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
