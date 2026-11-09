using Zaagl.Modules.Billing.Contracts;

namespace Zaagl.Links.Tests;

/// <summary>Test double for C5 (BE-003): a fixed plan with the configured Phase 1 values (appsettings.json "Plans").</summary>
public sealed class FixedContentPlanProvider(PlanTier tier) : IContentPlanProvider
{
    public static ContentPlan For(PlanTier tier) => tier == PlanTier.Starter
        ? new(PlanTier.Starter, false,
            new ContentLimits(104_857_600, 2_097_152, 30, true, 5_368_709_120),
            new PlanCapabilities(IsPaid: true, ApiKeys: true, KeepStatsAfterPurge: true, BurnAfterDownload: true,
                PasswordOnContent: true, ContentStatistics: true, ManageContent: true))
        : new(PlanTier.Free, false,
            new ContentLimits(26_214_400, 524_288, 7, true, 262_144_000),
            new PlanCapabilities(IsPaid: false, ApiKeys: false, KeepStatsAfterPurge: false, BurnAfterDownload: false,
                PasswordOnContent: false, ContentStatistics: true, ManageContent: true));

    public Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(For(tier));

    public ContentPlan Guest { get; } = new(null, true,
        new ContentLimits(26_214_400, 262_144, 7, false, null),
        new PlanCapabilities(false, false, false, false, false, false, false));
}
