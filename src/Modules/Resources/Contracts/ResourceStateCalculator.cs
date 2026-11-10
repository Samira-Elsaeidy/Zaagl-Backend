using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Modules.Resources.Contracts;

/// <summary>
/// ARC-006 §2 / addendum §2.4: total function, first match wins. The link state is authoritative (from
/// <c>AccessGateResult.LinkState</c> on public routes, from the snapshot on owner routes); a resource with no link
/// is purgeable. Never stored.
/// </summary>
public static class ResourceStateCalculator
{
    public static ResourceState Compute(
        LinkState? linkState, ProcessingStatus processingStatus, DateTime? burnClaimedAt, DateTime? deletedAt)
    {
        if (linkState is null or LinkState.Purgeable) return ResourceState.Purgeable;
        if (linkState == LinkState.Deleted || deletedAt is not null) return ResourceState.Deleted;
        if (linkState == LinkState.Disabled) return ResourceState.Disabled;
        if (burnClaimedAt is not null) return ResourceState.Burned;
        if (linkState == LinkState.Quarantined) return ResourceState.Expired;
        if (processingStatus is ProcessingStatus.Pending or ProcessingStatus.Scanning or ProcessingStatus.Error)
            return ResourceState.Processing;
        if (processingStatus == ProcessingStatus.Rejected) return ResourceState.Rejected;
        return ResourceState.Ready;
    }
}
