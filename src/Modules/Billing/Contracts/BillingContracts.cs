namespace Zaagl.Modules.Billing.Contracts;

public enum PlanTier
{
    Free = 0,
    Starter = 1,
}

/// <param name="MonthlyLinks">Null means unlimited.</param>
/// <param name="AnalyticsRetentionDays">Null means unlimited.</param>
/// <param name="AdvancedGates">Password, click limits and geo / device / A-B routing.</param>
public sealed record PlanLimits(
    PlanTier Tier,
    int? MonthlyLinks,
    bool CustomSlugs,
    int? AnalyticsRetentionDays,
    bool AdvancedGates)
{
    public static PlanLimits For(PlanTier tier) => tier switch
    {
        PlanTier.Starter => new(PlanTier.Starter, null, true, 365, true),
        _ => new(PlanTier.Free, 50, false, 7, false),
    };
}

public interface IPlanProvider
{
    Task<PlanLimits> GetLimitsAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record BillingStatusDto(PlanTier Tier, PlanLimits Limits, bool HasStripeCustomer);

public sealed record RedirectUrlDto(string Url);
