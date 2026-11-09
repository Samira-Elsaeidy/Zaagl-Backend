namespace Zaagl.Modules.Billing.Contracts;

public enum PlanTier
{
    Free = 0,
    Starter = 1,
}

/// <param name="MonthlyLinks">Null means unlimited.</param>
/// <param name="AnalyticsRetentionDays">Null means unlimited.</param>
/// <param name="AdvancedGates">Password, click limits and geo / device / A-B routing.</param>
/// <param name="DefaultExpiryDays">Spec §1.1: how far out a new link expires when the caller doesn't ask
/// for a specific date.</param>
/// <param name="MaxExpiryDays">Spec §1.1: the furthest out a link (or a renewal) may set its expiry.
/// Null means no cap (not used by either plan today, kept for symmetry with DefaultExpiryDays).</param>
public sealed record PlanLimits(
    PlanTier Tier,
    int? MonthlyLinks,
    bool CustomSlugs,
    int? AnalyticsRetentionDays,
    bool AdvancedGates,
    int DefaultExpiryDays = 30,
    int? MaxExpiryDays = 90)
{
    public static PlanLimits For(PlanTier tier) => tier switch
    {
        PlanTier.Starter => new(PlanTier.Starter, null, true, 365, true, DefaultExpiryDays: 365, MaxExpiryDays: 1095),
        _ => new(PlanTier.Free, 50, false, 7, false, DefaultExpiryDays: 30, MaxExpiryDays: 90),
    };

    /// <summary>Spec §7.12: guests get generated links and custom aliases, a fixed 7-day expiry, and no
    /// premium gates (password, click limits, routing rules) - their own per-IP quota replaces MonthlyLinks.</summary>
    public static PlanLimits Guest { get; } = new(PlanTier.Free, null, true, null, false, DefaultExpiryDays: 7, MaxExpiryDays: 7);
}

public interface IPlanProvider
{
    Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec §7.4/§8.2 (BE-003, PD-58): called by Billing after a user's plan changed (Stripe webhook), with the
/// capabilities before and after. Identity implements it to revoke every API key whenever
/// <see cref="PlanCapabilities.ApiKeys"/> of the NEW plan is false and to queue a security notice. Called after
/// Billing's own commit (separate module schemas, so not one transaction): Billing records a pending marker in the
/// same transaction as the tier change and retries it on each later billing event and every 5 minutes until every
/// listener succeeded. So a listener can be called again for the same change (or for an event that kept the tier)
/// and MUST be idempotent. Key authentication also re-checks the plan, so a failed call cannot keep a key working.
/// Added after the C5 freeze: needs architect review (contracts.md change rule).
/// </summary>
public interface IPlanChangeListener
{
    Task OnPlanChangedAsync(Guid userId, PlanCapabilities previous, PlanCapabilities current, CancellationToken cancellationToken);
}

/// <param name="Capabilities">C5 (Phase 1): what the plan allows, so clients stop comparing tiers
/// (design/ARC-005-plan-model-v2.md). Filled by BE-003.</param>
public sealed record BillingStatusDto(PlanTier Tier, PlanLimits Limits, bool HasStripeCustomer, PlanCapabilities? Capabilities = null);

public sealed record RedirectUrlDto(string Url);
