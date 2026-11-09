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

/// <summary>PD-58 (spec §7.4): a plan change whose listeners (API-key revocation in Identity) have not yet run
/// successfully. Written in the same transaction as the tier change, deleted once every listener succeeded, and
/// retried on each later billing event and by <c>PlanChangeRetryJob</c>. One row per user.</summary>
public sealed class PendingPlanChange
{
    public Guid UserId { get; set; }
    /// <summary>The tier before the oldest change not yet delivered (kept when a newer change is queued).</summary>
    public PlanTier PreviousTier { get; set; }
    public DateTime QueuedAt { get; set; }
    /// <summary>Bumped on every enqueue, so a delivery deletes the row only if no newer change arrived meanwhile.</summary>
    public int Version { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastAttemptAt { get; set; }
}
