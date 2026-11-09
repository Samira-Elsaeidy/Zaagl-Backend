using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Modules.Access.Application;

public enum AccessOutcome
{
    Allowed,
    Locked,
    /// <summary>Spec §4.3 "quarantined" (past expires_at). 410, or 302 to the owner's fallback URL (PD-42).</summary>
    Expired,
    /// <summary>Spec §4.3 state "purgeable": a caller must treat this exactly like the link never
    /// existed (404), distinct from Expired's 410-with-a-fallback-page behaviour.</summary>
    Purgeable,
    /// <summary>Spec §4.3 "deleted": 410 page, never the fallback URL (PD-42).</summary>
    Deleted,
    /// <summary>Spec §4.3 "disabled" (user-disabled, admin, abuse, safety): 410 page, never the fallback URL (PD-42).</summary>
    Disabled,
}

public static class AccessOutcomes
{
    /// <summary>True for every outcome in which the link no longer redirects to its destination.</summary>
    public static bool IsGone(this AccessOutcome outcome) =>
        outcome is AccessOutcome.Expired or AccessOutcome.Purgeable or AccessOutcome.Deleted or AccessOutcome.Disabled;
}

/// <summary>
/// Decides whether a visitor may pass a link's gates. Click limits are not decided here:
/// they are enforced atomically in PostgreSQL by <see cref="ILinkResolver.TryConsumeClickAsync"/>.
/// </summary>
public sealed class AccessGate
{
    public AccessOutcome Evaluate(LinkSnapshot link, DateTimeOffset now)
    {
        var nowUtc = now.UtcDateTime;
        var state = LinkStateCalculator.Compute(link, nowUtc);

        // Spec §4.3 / §7.3 (PD-39): purgeable → 404; deleted, disabled and quarantined → 410, each with its own
        // page. Only quarantined (expired) may use the owner's fallback URL (PD-42).
        switch (state)
        {
            case LinkState.Purgeable: return AccessOutcome.Purgeable;
            case LinkState.Deleted: return AccessOutcome.Deleted;
            case LinkState.Disabled: return AccessOutcome.Disabled;
        }
        // IsActive=false without DisabledAt set is a defensive fallback (a row touched by older code or a
        // direct edit that didn't go through SetFlagsAsync): still disabled, and it wins over expiry.
        if (!link.IsActive) return AccessOutcome.Disabled;
        if (state == LinkState.Quarantined) return AccessOutcome.Expired;
        return link.HasPassword ? AccessOutcome.Locked : AccessOutcome.Allowed;
    }

    public bool VerifyPassword(LinkSnapshot link, string? password) =>
        link.HasPassword
        && !string.IsNullOrEmpty(password)
        && BCrypt.Net.BCrypt.Verify(password, link.PasswordHash);
}
