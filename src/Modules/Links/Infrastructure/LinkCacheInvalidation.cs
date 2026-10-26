using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zaagl.Modules.Links.Infrastructure;

/// <summary>
/// Spec §7.3: every transaction that changes a link notifies every API instance so they drop their
/// cached copy, instead of waiting for a TTL. This app is one instance in dev and already removes the
/// changed entry from its own in-process/Valkey cache synchronously (see <c>LinkService</c>'s
/// <c>cache.RemoveAsync</c> calls) - that remains the primary, always-correct mechanism here. This NOTIFY
/// path is the genuine spec-shaped mechanism for when there are N instances sharing one Postgres: it is
/// wired up for real (not a stub) so a future multi-instance deployment gets it for free, but in this
/// single-instance dev environment it is a secondary/best-effort signal, not load-bearing.
/// </summary>
public interface ILinkCacheInvalidator
{
    /// <summary>Notifies every listening instance that the given slug keys changed. Payload is kept
    /// under 4,000 bytes per spec §7.3 (PostgreSQL's own NOTIFY limit is under 8,000); callers with more
    /// than fit in one message should call this once per chunk.</summary>
    Task NotifyChangedAsync(IReadOnlyCollection<string> slugKeys, CancellationToken ct);
}

public sealed class PgNotifyLinkCacheInvalidator(NpgsqlDataSource dataSource) : ILinkCacheInvalidator
{
    private const int MaxPayloadBytes = 4000;

    public async Task NotifyChangedAsync(IReadOnlyCollection<string> slugKeys, CancellationToken ct)
    {
        if (slugKeys.Count == 0) return;

        foreach (var chunk in ChunkToFit(slugKeys))
        {
            var payload = JsonSerializer.Serialize(chunk);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_notify('zaagl_link_cache', $1)";
            cmd.Parameters.AddWithValue(payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Splits into JSON-array chunks that each stay under the byte limit, so one bulk change
    /// (e.g. a future takedown) never produces an oversized NOTIFY payload.</summary>
    private static IEnumerable<List<string>> ChunkToFit(IReadOnlyCollection<string> keys)
    {
        var chunk = new List<string>();
        var size = 2; // "[]"
        foreach (var key in keys)
        {
            var added = JsonSerializer.Serialize(key).Length + 1; // +1 for the comma/bracket
            if (chunk.Count > 0 && size + added > MaxPayloadBytes)
            {
                yield return chunk;
                chunk = [];
                size = 2;
            }
            chunk.Add(key);
            size += added;
        }
        if (chunk.Count > 0) yield return chunk;
    }
}

/// <summary>No-op default so <c>ILinkCacheInvalidator</c> can be injected everywhere without every
/// environment needing a live LISTEN/NOTIFY connection (e.g. unit tests with no real Postgres).</summary>
public sealed class NoOpLinkCacheInvalidator : ILinkCacheInvalidator
{
    public Task NotifyChangedAsync(IReadOnlyCollection<string> slugKeys, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Listens for <c>zaagl_link_cache</c> notifications on a dedicated, non-pooled connection (spec §7.3)
/// and drops the matching entries from the shared HybridCache, so a disabled/deleted/renewed link stops
/// being served from cache on every instance as soon as the change commits - not just the instance that
/// made the change. Also sends a liveness ping every 5s on the same connection; if ever more than one
/// instance runs this becomes meaningful (today, with one instance, the ping is a no-op heartbeat only).
/// </summary>
public sealed class LinkCacheInvalidationListener(
    NpgsqlDataSource dataSource,
    Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
    Microsoft.Extensions.Logging.ILogger<LinkCacheInvalidationListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                conn.Notification += async (_, e) =>
                {
                    try
                    {
                        var keys = JsonSerializer.Deserialize<string[]>(e.Payload) ?? [];
                        foreach (var key in keys)
                            await cache.RemoveAsync($"link:{key}", stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not process a link-cache invalidation payload.");
                    }
                };

                await using (var listen = conn.CreateCommand())
                {
                    listen.CommandText = "LISTEN zaagl_link_cache";
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Link-cache invalidation listener dropped; reconnecting in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
