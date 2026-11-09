using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Billing.Infrastructure;

namespace Zaagl.Modules.Billing.Application;

// BE-003 (design/ARC-005-plan-model-v2.md, C5). Plan values live in configuration ("Plans" section) and are
// validated at startup. The tier -> plan mapping below is the ONE place outside BillingService that switches on a
// PlanTier; every other module reads ContentLimits / PlanCapabilities only (addendum §10.2). A grep/arch test that
// forbids PlanTier comparisons outside src/Modules/Billing is QA/ARC-017 work, deferred by PD-51.

public sealed class PlanContentOptions
{
    public long? MaxFileBytes { get; set; }
    public long? MaxSnippetBytes { get; set; }
    public int? RetentionDays { get; set; }
    public bool? QuarantineAfterExpiry { get; set; }
    public long? StorageQuotaBytes { get; set; }
}

public sealed class PlanCapabilityOptions
{
    public bool? IsPaid { get; set; }
    public bool? ApiKeys { get; set; }
    public bool? KeepStatsAfterPurge { get; set; }
    public bool? BurnAfterDownload { get; set; }
    public bool? PasswordOnContent { get; set; }
    public bool? ContentStatistics { get; set; }
    public bool? ManageContent { get; set; }
}

public sealed class PlanOptions
{
    public PlanContentOptions? Content { get; set; }
    public PlanCapabilityOptions? Capabilities { get; set; }
}

public sealed class PlansOptions
{
    public const string Section = "Plans";

    /// <summary>Phase 1 maximum per file (addendum §7.1.4): 100 MB, so "too large to scan" cannot occur.</summary>
    public const long MaxFileBytesCeiling = 104_857_600;

    public PlanOptions? Guest { get; set; }
    public PlanOptions? Free { get; set; }
    public PlanOptions? Starter { get; set; }
}

/// <summary>ARC-005 §2 startup validation. Fails startup (ValidateOnStart) on a missing or invalid section.</summary>
public sealed class PlansOptionsValidator : IValidateOptions<PlansOptions>
{
    public ValidateOptionsResult Validate(string? name, PlansOptions options)
    {
        var errors = new List<string>();
        Check("Guest", options.Guest, isGuest: true, errors);
        Check("Free", options.Free, isGuest: false, errors);
        Check("Starter", options.Starter, isGuest: false, errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Check(string plan, PlanOptions? p, bool isGuest, List<string> errors)
    {
        var prefix = $"{PlansOptions.Section}:{plan}";
        if (p?.Content is not { } c) { errors.Add($"{prefix}:Content is missing."); }
        else
        {
            if (c.MaxFileBytes is not { } max || max <= 0 || max > PlansOptions.MaxFileBytesCeiling)
                errors.Add($"{prefix}:Content:MaxFileBytes must be between 1 and {PlansOptions.MaxFileBytesCeiling}.");
            if (c.MaxSnippetBytes is not { } snip || snip < 0)
                errors.Add($"{prefix}:Content:MaxSnippetBytes must be 0 or more (0 = snippets refused).");
            if (c.RetentionDays is not { } days || days < 1)
                errors.Add($"{prefix}:Content:RetentionDays must be at least 1.");
            if (c.QuarantineAfterExpiry is null)
                errors.Add($"{prefix}:Content:QuarantineAfterExpiry is missing.");
            if (isGuest)
            {
                if (c.QuarantineAfterExpiry == true) errors.Add($"{prefix}:Content:QuarantineAfterExpiry must be false for guests.");
                if (c.StorageQuotaBytes is not null) errors.Add($"{prefix}:Content:StorageQuotaBytes must be null for guests.");
            }
            else if (c.StorageQuotaBytes is not { } quota || quota <= 0)
            {
                errors.Add($"{prefix}:Content:StorageQuotaBytes must be set (> 0) for account plans.");
            }
        }

        if (p?.Capabilities is not { } k) { errors.Add($"{prefix}:Capabilities is missing."); return; }
        foreach (var (field, value) in new (string, bool?)[]
                 {
                     ("IsPaid", k.IsPaid), ("ApiKeys", k.ApiKeys), ("KeepStatsAfterPurge", k.KeepStatsAfterPurge),
                     ("BurnAfterDownload", k.BurnAfterDownload), ("PasswordOnContent", k.PasswordOnContent),
                     ("ContentStatistics", k.ContentStatistics), ("ManageContent", k.ManageContent),
                 })
        {
            if (value is null) errors.Add($"{prefix}:Capabilities:{field} is missing.");
        }
    }
}

/// <summary>Maps a tier (or "guest") to its configured <see cref="ContentPlan"/>. Reads IOptionsMonitor on every
/// call, so a configuration change applies without code (ARC-005 §3).</summary>
public sealed class PlanCatalog(IOptionsMonitor<PlansOptions> options)
{
    public ContentPlan Guest => Build(null, options.CurrentValue.Guest!);

    public ContentPlan For(PlanTier tier) => tier switch
    {
        PlanTier.Starter => Build(PlanTier.Starter, options.CurrentValue.Starter!),
        _ => Build(PlanTier.Free, options.CurrentValue.Free!),
    };

    private static ContentPlan Build(PlanTier? tier, PlanOptions p)
    {
        var c = p.Content!;
        var k = p.Capabilities!;
        return new ContentPlan(
            tier,
            IsGuest: tier is null,
            new ContentLimits(c.MaxFileBytes!.Value, c.MaxSnippetBytes!.Value, c.RetentionDays!.Value,
                c.QuarantineAfterExpiry!.Value, c.StorageQuotaBytes),
            new PlanCapabilities(k.IsPaid!.Value, k.ApiKeys!.Value, k.KeepStatsAfterPurge!.Value,
                k.BurnAfterDownload!.Value, k.PasswordOnContent!.Value, k.ContentStatistics!.Value, k.ManageContent!.Value));
    }
}

/// <summary>C5 provider. One indexed read of <c>billing.subscriptions</c> per user, cached locally for 60 seconds
/// (the same bound as the security-stamp cache); the Stripe webhook removes the entry on a plan change.</summary>
public sealed class ContentPlanProvider(BillingDbContext db, PlanCatalog catalog, IMemoryCache cache) : IContentPlanProvider
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    internal static string CacheKey(Guid userId) => $"billing:tier:{userId:N}";

    public ContentPlan Guest => catalog.Guest;

    public async Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Only the tier is cached, so a configuration change to the plan values applies immediately.
        if (!cache.TryGetValue(CacheKey(userId), out PlanTier tier))
        {
            tier = await db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => (PlanTier?)s.Tier)
                .FirstOrDefaultAsync(cancellationToken) ?? PlanTier.Free;
            cache.Set(CacheKey(userId), tier, CacheLifetime);
        }
        return catalog.For(tier);
    }

    /// <summary>Called by the webhook after a subscription change (this instance only; others age out in 60 s).</summary>
    public static void Invalidate(IMemoryCache cache, Guid userId) => cache.Remove(CacheKey(userId));
}
