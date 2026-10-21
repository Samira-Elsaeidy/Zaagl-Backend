using Zaagl.Modules.Billing.Contracts;

namespace Zaagl.Modules.Billing.Domain;

public sealed class Subscription
{
    public Guid UserId { get; set; }
    public PlanTier Tier { get; set; } = PlanTier.Free;
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? Status { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Processed Stripe event ids, so redelivered webhooks are ignored.</summary>
public sealed class ProcessedStripeEvent
{
    public string EventId { get; set; } = "";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
