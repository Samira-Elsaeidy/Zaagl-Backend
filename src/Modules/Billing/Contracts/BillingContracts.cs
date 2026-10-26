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

public sealed record BillingStatusDto(PlanTier Tier, PlanLimits Limits, bool HasStripeCustomer);

public sealed record RedirectUrlDto(string Url);
