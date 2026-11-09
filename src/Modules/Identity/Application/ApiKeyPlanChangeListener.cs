using Zaagl.Modules.Billing.Contracts;

namespace Zaagl.Modules.Identity.Application;

/// <summary>Spec §7.4 (BE-003, PD-58): revokes every API key whenever the user's new plan lacks the <c>ApiKeys</c>
/// capability, whatever the previous plan was (Billing also calls this on retries and same-tier events; the
/// revocation is idempotent and sends its notice only when it revoked something).</summary>
public sealed class ApiKeyPlanChangeListener(IdentityService identity) : IPlanChangeListener
{
    public Task OnPlanChangedAsync(Guid userId, PlanCapabilities previous, PlanCapabilities current, CancellationToken cancellationToken) =>
        !current.ApiKeys
            ? identity.RevokeApiKeysForPlanChangeAsync(userId, cancellationToken)
            : Task.CompletedTask;
}
