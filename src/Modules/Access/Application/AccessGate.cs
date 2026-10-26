using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Modules.Access.Application;

public enum AccessOutcome
{
    Allowed,
    Locked,
    Expired,
    /// <summary>Spec §4.3 state "purgeable": a caller must treat this exactly like the link never
    /// existed (404), distinct from Expired's 410-with-a-fallback-page behaviour.</summary>
    Purgeable,
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

        // Purgeable/Deleted/Disabled/Quarantined all mean "this link no longer redirects" - the spec's
        // state table (§4.3) maps the first three to 410 and purgeable to 404; this app's existing
        // ResolveKind.Expired already renders a "no longer available" page with an optional fallback URL,
        // so Deleted/Disabled/Quarantined all map onto it (deliberate simplification: distinct HTTP
        // semantics for each are a later phase once there's a reason for a visitor to tell them apart).
        // IsActive remains a legacy synonym for "disabled by the owner" (SetFlagsAsync keeps it and
        // DisabledKind.UserDisabled in sync), so this check no longer needs to look at it separately.
        if (state == LinkState.Purgeable) return AccessOutcome.Purgeable;
        // IsActive=false without DisabledAt set is a defensive fallback (e.g. a row touched by older code
        // or a direct edit that didn't go through SetFlagsAsync) - still treated as disabled.
        if (state is LinkState.Deleted or LinkState.Disabled or LinkState.Quarantined || !link.IsActive)
            return AccessOutcome.Expired;
        return link.HasPassword ? AccessOutcome.Locked : AccessOutcome.Allowed;
    }

    public bool VerifyPassword(LinkSnapshot link, string? password) =>
        link.HasPassword
        && !string.IsNullOrEmpty(password)
        && BCrypt.Net.BCrypt.Verify(password, link.PasswordHash);
}
