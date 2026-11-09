namespace Zaagl.Modules.Billing.Contracts;

// Phase 1 contract C5 (docs/phase1-implementation/contracts.md, ADR-0004, addendum §10).
// NEW types only: PlanLimits and PlanTier keep their Phase 0 behaviour. PD-04: Free and Starter only, no Pro.
// Values come from configuration (Plans:*:Content:*); none are hard-coded here.

/// <summary>
/// Capability flags. Callers ask "may this plan do X?" instead of comparing <see cref="PlanTier"/> values
/// (BE-003 removes every <c>Tier == PlanTier.Starter</c> outside Billing).
/// </summary>
/// <param name="IsPaid">Any paid plan (Starter in Phase 1).</param>
/// <param name="ApiKeys">May create API keys (PD-07: paid only). Keys stop working when this turns false.</param>
/// <param name="KeepStatsAfterPurge">Daily statistics are archived at purge (spec §1.1 premium rule).</param>
/// <param name="BurnAfterDownload">May create burn-after-download files. Starter only (PD-54); false for Free and guests.</param>
/// <param name="PasswordOnContent">May set a password on content. Starter only (PD-54); false for Free and guests.</param>
/// <param name="ContentStatistics">Content views/downloads are recorded per link (false for guests).</param>
/// <param name="ManageContent">May renew, edit or delete own content (false for guests, spec §7.12).</param>
public sealed record PlanCapabilities(
    bool IsPaid,
    bool ApiKeys,
    bool KeepStatsAfterPurge,
    bool BurnAfterDownload,
    bool PasswordOnContent,
    bool ContentStatistics,
    bool ManageContent);

/// <summary>Content limits of one plan (or of guests).</summary>
/// <param name="MaxFileBytes">Per file or media item. Guest 25 MB (PD-54), Free 25 MB, Starter 100 MB (PD-04).
/// 1 MB = 1,048,576 bytes (PD-54).</param>
/// <param name="MaxSnippetBytes">Per snippet version: guest 256 KB, Free 512 KB, Starter 2 MB (PD-56; 1 KB = 1,024 bytes).</param>
/// <param name="RetentionDays">Default and maximum content expiry. Free 7, Starter 30, guest 7.</param>
/// <param name="QuarantineAfterExpiry">True for accounts (bytes kept until purge_after, PD-06); false for guests.</param>
/// <param name="StorageQuotaBytes">Per-account total: Free 250 MB, Starter 5 GB (PD-54). Null for guests
/// (guests are limited by creation counts instead).</param>
public sealed record ContentLimits(
    long MaxFileBytes,
    long MaxSnippetBytes,
    int RetentionDays,
    bool QuarantineAfterExpiry,
    long? StorageQuotaBytes);

/// <param name="Tier">Null for guests.</param>
public sealed record ContentPlan(PlanTier? Tier, bool IsGuest, ContentLimits Limits, PlanCapabilities Capabilities);

/// <summary>C5: read when a content rule applies (upload create, snippet create, renewal, ticket mint).</summary>
public interface IContentPlanProvider
{
    Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Spec §7.12 guest rules applied to content (addendum §11).</summary>
    ContentPlan Guest { get; }
}
