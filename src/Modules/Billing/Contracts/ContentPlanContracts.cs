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

/// <summary>
/// Phase 2 (E2.1, E2.2) tool limits of one plan (or of guests). PROPOSED values (docs/phase2/README.md "Plan gating"): the PRD puts
/// the power tools in a Pro tier that does not exist in Phase 1 (PD-04), so every value is configuration
/// (<c>Plans:&lt;Plan&gt;:Tools:*</c>) with the proposed defaults of <see cref="Defaults"/>; nothing here is an approved decision.
/// A zero count or a false flag means "not on this plan" (callers answer <c>402 plan_required</c>, or 401 for a guest).
/// </summary>
/// <param name="VaultEnabled">May create an end-to-end encrypted secret (E2.1 vault).</param>
/// <param name="VaultMaxCiphertextBytes">Largest ciphertext envelope accepted, in bytes (after base64url decoding).</param>
/// <param name="VaultMaxViews">Largest view limit (1 = burn after the first reveal).</param>
/// <param name="HookMaxActive">Live webhook catchers at once (0 = not available).</param>
/// <param name="HookMaxRequests">Captured requests kept per catcher; the oldest are dropped beyond it.</param>
/// <param name="HookMaxBodyBytes">Bytes of each request body stored (the rest is counted, not stored).</param>
/// <param name="HookLifetimeHours">A catcher's lifetime from creation (and its maximum).</param>
/// <param name="HookIngestPerMinute">Requests accepted per catcher and minute (429 beyond).</param>
/// <param name="CompareEnabled">May create a before/after comparison link (E2.2).</param>
/// <param name="CapsulesEnabled">May create a bug capsule (E2.1).</param>
/// <param name="CapsuleMaxLogBytes">Largest console + network log document of a capsule, in UTF-8 bytes.</param>
/// <param name="CommentsEnabled">May turn on pinned comments for their own content (E2.2). Commenting itself is open to link holders.</param>
/// <param name="ApprovalEnabled">May create a watermarked approval link (E2.2).</param>
/// <param name="HookMaxStoredBytes">Total captured bytes kept per catcher; the oldest captures are dropped beyond it.</param>
public sealed record ToolLimits(
    bool VaultEnabled,
    int VaultMaxCiphertextBytes,
    int VaultMaxViews,
    int HookMaxActive,
    int HookMaxRequests,
    int HookMaxBodyBytes,
    int HookLifetimeHours,
    int HookIngestPerMinute,
    bool CompareEnabled,
    bool CapsulesEnabled,
    int CapsuleMaxLogBytes,
    bool CommentsEnabled,
    bool ApprovalEnabled,
    long HookMaxStoredBytes = 0)
{
    /// <summary>The proposed defaults per plan (1 KB = 1,024 bytes, PD-54).</summary>
    public static class Defaults
    {
        public static readonly ToolLimits Guest = new(
            VaultEnabled: true, VaultMaxCiphertextBytes: 16 * 1024, VaultMaxViews: 1,
            HookMaxActive: 0, HookMaxRequests: 0, HookMaxBodyBytes: 0, HookLifetimeHours: 0, HookIngestPerMinute: 0,
            CompareEnabled: false, CapsulesEnabled: false, CapsuleMaxLogBytes: 0, CommentsEnabled: false, ApprovalEnabled: false,
            HookMaxStoredBytes: 0);

        public static readonly ToolLimits Free = new(
            VaultEnabled: true, VaultMaxCiphertextBytes: 64 * 1024, VaultMaxViews: 1,
            HookMaxActive: 1, HookMaxRequests: 100, HookMaxBodyBytes: 64 * 1024, HookLifetimeHours: 24, HookIngestPerMinute: 30,
            CompareEnabled: true, CapsulesEnabled: true, CapsuleMaxLogBytes: 512 * 1024, CommentsEnabled: true, ApprovalEnabled: false,
            HookMaxStoredBytes: 5L * 1024 * 1024);

        public static readonly ToolLimits Starter = new(
            VaultEnabled: true, VaultMaxCiphertextBytes: 256 * 1024, VaultMaxViews: 10,
            HookMaxActive: 10, HookMaxRequests: 1000, HookMaxBodyBytes: 512 * 1024, HookLifetimeHours: 168, HookIngestPerMinute: 120,
            CompareEnabled: true, CapsulesEnabled: true, CapsuleMaxLogBytes: 2 * 1024 * 1024, CommentsEnabled: true, ApprovalEnabled: true,
            HookMaxStoredBytes: 50L * 1024 * 1024);
    }
}

/// <param name="Tier">Null for guests.</param>
/// <param name="Tools">Phase 2 tool limits; always set by the Billing catalog. Null only in hand-built plans, read through
/// <see cref="ToolsOrNone"/> as the guest defaults (the most restrictive).</param>
public sealed record ContentPlan(PlanTier? Tier, bool IsGuest, ContentLimits Limits, PlanCapabilities Capabilities, ToolLimits? Tools = null)
{
    public ToolLimits ToolsOrNone => Tools ?? ToolLimits.Defaults.Guest;
}

/// <summary>C5: read when a content rule applies (upload create, snippet create, renewal, ticket mint).</summary>
public interface IContentPlanProvider
{
    Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Spec §7.12 guest rules applied to content (addendum §11).</summary>
    ContentPlan Guest { get; }
}
