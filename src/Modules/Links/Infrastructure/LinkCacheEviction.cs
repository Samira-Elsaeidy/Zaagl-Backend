using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Zaagl.Modules.Links.Infrastructure;

public static class LinkCacheMetrics
{
    private static readonly Meter Meter = new("Zaagl.Links.Cache");

    /// <summary>Post-commit cache invalidation steps that failed. Tag <c>stage</c>: <c>cache</c> (local/L2
    /// removal) or <c>notify</c> (the pg_notify to other instances). The change itself was committed.</summary>
    public static readonly Counter<long> InvalidationFailuresTotal = Meter.CreateCounter<long>("link_cache_invalidation_failures_total");
}

/// <summary>
/// Runs after a link change has committed. The change is already durable, so nothing here may turn a
/// successful create/update/delete into an error (a Valkey timeout once did, after the row was saved):
/// every step is best effort, logged and counted, and runs on its own token so a client that disconnects
/// does not skip the eviction. Correctness does not rest on this alone: the redirect state is recomputed on
/// every hit (spec §7.3, R5), the L1 copy is dropped by HybridCache before it touches L2, and the L2 layer
/// remembers keys whose delete was lost (<c>ResilientDistributedCache</c>).
/// </summary>
public static class LinkCacheEviction
{
    public static async Task EvictAfterCommitAsync(
        HybridCache? cache, ILinkCacheInvalidator invalidator, IReadOnlyCollection<string> slugKeys, ILogger logger)
    {
        if (slugKeys.Count == 0) return;

        if (cache is not null)
        {
            foreach (var key in slugKeys)
            {
                try
                {
                    await cache.RemoveAsync(Application.LinkCache.Key(key), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LinkCacheMetrics.InvalidationFailuresTotal.Add(1, new KeyValuePair<string, object?>("stage", "cache"));
                    logger.LogWarning(ex, "Link {SlugKey} changed but its cache entry could not be removed; the change is committed.", key);
                }
            }
        }

        try
        {
            await invalidator.NotifyChangedAsync(slugKeys, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LinkCacheMetrics.InvalidationFailuresTotal.Add(1, new KeyValuePair<string, object?>("stage", "notify"));
            logger.LogWarning(ex, "{Count} link(s) changed but the cache invalidation NOTIFY failed; the change is committed.", slugKeys.Count);
        }
    }
}
