using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Zaagl.Api.Infrastructure;

/// <summary>Settings for the shared (L2, Valkey) layer under HybridCache. Section <c>Cache:L2</c>.</summary>
public sealed class L2CacheOptions
{
    public const string Section = "Cache:L2";

    /// <summary>Longest wait for one L2 operation before it counts as failed and the caller carries on
    /// without L2 (a read becomes a miss, so the redirect falls back to the <c>redirects</c> pool).</summary>
    public int OperationTimeoutMs { get; set; } = 250;

    /// <summary>Consecutive failed L2 operations that open the circuit.</summary>
    public int FailuresBeforeBreak { get; set; } = 1;

    /// <summary>How long an open circuit skips L2 entirely; afterwards one probe operation is let through.</summary>
    public int BreakSeconds { get; set; } = 30;

    /// <summary>Must be at least the longest HybridCache <c>Expiration</c> written to L2 (link snapshots: 5 min).
    /// A key whose L2 delete failed is never read from L2 for this long (it would be stale), unless a later
    /// delete or write of that key succeeds first.</summary>
    public int MaxEntryLifetimeSeconds { get; set; } = 300;

    /// <summary>Upper bound on remembered failed deletes. Past it, every L2 read is skipped for
    /// <see cref="MaxEntryLifetimeSeconds"/> instead (always safe, only slower).</summary>
    public int MaxPendingRemovals { get; set; } = 100_000;
}

public static class L2CacheMetrics
{
    private static readonly Meter Meter = new("Zaagl.Cache");
    /// <summary>L2 operations that failed or timed out. Tag <c>op</c>: get, set, remove, refresh.</summary>
    public static readonly Counter<long> FailuresTotal = Meter.CreateCounter<long>("cache_l2_failures_total");
    /// <summary>L2 operations not attempted because the circuit was open (or the key's L2 copy is distrusted).</summary>
    public static readonly Counter<long> SkippedTotal = Meter.CreateCounter<long>("cache_l2_skipped_total");
    public static readonly Counter<long> CircuitOpenedTotal = Meter.CreateCounter<long>("cache_l2_circuit_opened_total");
}

/// <summary>
/// Wraps the L2 <see cref="IDistributedCache"/> (Valkey) so its outage can never fail or slow a request.
/// Every operation is bounded by <see cref="L2CacheOptions.OperationTimeoutMs"/> and never throws (except for
/// the caller's own cancellation of a read); after <see cref="L2CacheOptions.FailuresBeforeBreak"/> failures a
/// circuit breaker skips L2 for <see cref="L2CacheOptions.BreakSeconds"/>, then lets one probe through.
///
/// Correctness (spec §7.3, R5): HybridCache drops its own L1 copy on <c>RemoveAsync</c> whatever L2 does, and
/// the redirect state is recomputed on every hit. What L2 can still hold after an outage is a copy whose delete
/// was lost; such keys are remembered here and treated as misses (with a retried delete) until a delete or a
/// fresh write of that key succeeds, or until the copy has certainly expired.
/// </summary>
public sealed class ResilientDistributedCache(
    IDistributedCache inner,
    IOptions<L2CacheOptions> options,
    TimeProvider time,
    ILogger<ResilientDistributedCache> logger) : IDistributedCache
{
    private readonly L2CacheOptions _o = options.Value;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingRemovals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _abandonedWrites = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _probeInFlight;
    private DateTimeOffset _distrustAllUntil = DateTimeOffset.MinValue;

    /// <summary>True while the circuit is open (L2 is being skipped).</summary>
    public bool IsOpen { get { lock (_gate) return _openUntil is not null; } }

    /// <summary>Keys whose L2 copy is currently distrusted because their delete failed.</summary>
    public int PendingRemovals => _pendingRemovals.Count;

    /// <summary>Keys whose L2 copy is currently distrusted because a write to them was abandoned (it may still land).</summary>
    public int AbandonedWrites => _abandonedWrites.Count;

    /// <summary>Replaces the registered <see cref="IDistributedCache"/> (e.g. the StackExchange.Redis one) with
    /// this wrapper around it.</summary>
    public static void Decorate(IServiceCollection services)
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(IDistributedCache))
            ?? throw new InvalidOperationException("Register an IDistributedCache before decorating it.");
        services.Remove(original);
        services.AddSingleton<IDistributedCache>(sp => new ResilientDistributedCache(
            Resolve(sp, original),
            sp.GetRequiredService<IOptions<L2CacheOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<ResilientDistributedCache>>()));
    }

    private static IDistributedCache Resolve(IServiceProvider sp, ServiceDescriptor d) =>
        (IDistributedCache)(d.ImplementationInstance
            ?? d.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));

    // ---- IDistributedCache --------------------------------------------------------------------------------

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        if (IsDistrusted(key))
        {
            L2CacheMetrics.SkippedTotal.Add(1, new KeyValuePair<string, object?>("op", "get"));
            await RetryPendingRemovalAsync(key);
            return null;
        }
        var (ok, value) = await RunAsync("get", c => inner.GetAsync(key, c), token, rethrowCallerCancellation: true);
        return ok ? value : null;
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        var (ok, _) = await RunAsync("set", async c => { await inner.SetAsync(key, value, options, c); return true; }, token);
        if (ok) _pendingRemovals.TryRemove(key, out _); // a fresh copy replaced the stale one
        // Head round 3 note 1: an abandoned (timed-out or failed) write may still reach Valkey later, after a delete
        // that already landed, and leave a stale copy for up to its lifetime. Unlike a lost delete, a later successful
        // delete or write cannot clear this (the late write may land after it), so the key is not read from L2 until
        // MaxEntryLifetimeSeconds have passed since the write was abandoned.
        else RememberAbandonedWrite(key);
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        // A delete must not be lost just because the caller went away: it runs on its own token, still bounded.
        var (ok, _) = await RunAsync("remove", async c => { await inner.RemoveAsync(key, c); return true; }, CancellationToken.None);
        if (ok) _pendingRemovals.TryRemove(key, out _);
        else RememberFailedRemoval(key);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default) =>
        await RunAsync("refresh", async c => { await inner.RefreshAsync(key, c); return true; }, token);

    // HybridCache only uses the async members; the sync ones share the same rules.
    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => SetAsync(key, value, options).GetAwaiter().GetResult();
    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    // ---- Stale-copy tracking ------------------------------------------------------------------------------

    private bool IsDistrusted(string key)
    {
        var now = time.GetUtcNow();
        DateTimeOffset distrustAllUntil;
        lock (_gate) distrustAllUntil = _distrustAllUntil;
        if (now < distrustAllUntil) return true;
        if (_abandonedWrites.TryGetValue(key, out var abandoned))
        {
            if (now - abandoned < TimeSpan.FromSeconds(_o.MaxEntryLifetimeSeconds)) return true;
            _abandonedWrites.TryRemove(key, out _); // a late write has expired by now
        }
        if (!_pendingRemovals.TryGetValue(key, out var since)) return false;
        if (now - since < TimeSpan.FromSeconds(_o.MaxEntryLifetimeSeconds)) return true;
        _pendingRemovals.TryRemove(key, out _); // any copy written before the failed delete has expired by now
        return false;
    }

    private async Task RetryPendingRemovalAsync(string key)
    {
        if (!_pendingRemovals.ContainsKey(key)) return;
        var (ok, _) = await RunAsync("remove", async c => { await inner.RemoveAsync(key, c); return true; }, CancellationToken.None);
        if (ok) _pendingRemovals.TryRemove(key, out _);
    }

    private void RememberAbandonedWrite(string key)
    {
        var now = time.GetUtcNow();
        var horizon = now - TimeSpan.FromSeconds(_o.MaxEntryLifetimeSeconds);
        if (_abandonedWrites.Count >= _o.MaxPendingRemovals)
            foreach (var (k, since) in _abandonedWrites)
                if (since <= horizon) _abandonedWrites.TryRemove(k, out _);
        if (_abandonedWrites.Count >= _o.MaxPendingRemovals)
        {
            lock (_gate) _distrustAllUntil = now + TimeSpan.FromSeconds(_o.MaxEntryLifetimeSeconds);
            _abandonedWrites.Clear();
            logger.LogWarning("L2 cache: more than {Max} abandoned writes; skipping every L2 read for {Seconds} s.",
                _o.MaxPendingRemovals, _o.MaxEntryLifetimeSeconds);
            return;
        }
        _abandonedWrites[key] = now;
    }

    private void RememberFailedRemoval(string key)
    {
        var now = time.GetUtcNow();
        if (_pendingRemovals.Count >= _o.MaxPendingRemovals)
        {
            var horizon = now - TimeSpan.FromSeconds(_o.MaxEntryLifetimeSeconds);
            foreach (var (k, since) in _pendingRemovals)
                if (since <= horizon) _pendingRemovals.TryRemove(k, out _);
        }
        if (_pendingRemovals.Count >= _o.MaxPendingRemovals)
        {
            // Too many to track one by one: distrust every L2 copy until all of them have expired.
            lock (_gate) _distrustAllUntil = now + TimeSpan.FromSeconds(_o.MaxEntryLifetimeSeconds);
            _pendingRemovals.Clear();
            logger.LogWarning("L2 cache: more than {Max} lost deletes; skipping every L2 read for {Seconds} s.",
                _o.MaxPendingRemovals, _o.MaxEntryLifetimeSeconds);
            return;
        }
        _pendingRemovals[key] = now; // restart the window: a copy may have been written up to now
    }

    // ---- Circuit breaker ----------------------------------------------------------------------------------

    private async Task<(bool Ok, T? Value)> RunAsync<T>(
        string op, Func<CancellationToken, Task<T>> action, CancellationToken token, bool rethrowCallerCancellation = false)
    {
        if (!TryEnter(out var probe))
        {
            L2CacheMetrics.SkippedTotal.Add(1, new KeyValuePair<string, object?>("op", op));
            return (false, default);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<T> task;
        try
        {
            task = action(cts.Token);
        }
        catch (Exception ex)
        {
            OnFailure(op, probe, ex);
            return (false, default);
        }

        try
        {
            var value = await task.WaitAsync(TimeSpan.FromMilliseconds(_o.OperationTimeoutMs), time, token);
            OnSuccess(probe);
            return (true, value);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Abandon(task, cts);
            ReleaseProbe(probe);
            if (rethrowCallerCancellation) throw;
            return (false, default);
        }
        catch (Exception ex)
        {
            Abandon(task, cts);
            OnFailure(op, probe, ex);
            return (false, default);
        }
    }

    private static void Abandon(Task task, CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        // Observe the abandoned operation's eventual fault so it never surfaces as an unobserved exception.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool TryEnter(out bool probe)
    {
        probe = false;
        lock (_gate)
        {
            if (_openUntil is not { } until) return true;
            if (time.GetUtcNow() < until || _probeInFlight) return false;
            _probeInFlight = probe = true; // half-open: exactly one operation tests whether L2 is back
            return true;
        }
    }

    private void ReleaseProbe(bool probe)
    {
        if (!probe) return;
        lock (_gate) _probeInFlight = false;
    }

    private void OnSuccess(bool probe)
    {
        bool recovered;
        lock (_gate)
        {
            recovered = _openUntil is not null && probe;
            _consecutiveFailures = 0;
            if (probe)
            {
                _probeInFlight = false;
                _openUntil = null;
            }
        }
        if (recovered) logger.LogInformation("L2 cache (Valkey) is reachable again; circuit closed.");
    }

    private void OnFailure(string op, bool probe, Exception ex)
    {
        L2CacheMetrics.FailuresTotal.Add(1, new KeyValuePair<string, object?>("op", op));
        bool opened;
        lock (_gate)
        {
            _consecutiveFailures++;
            var wasClosed = _openUntil is null;
            if (probe || _consecutiveFailures >= Math.Max(1, _o.FailuresBeforeBreak))
                _openUntil = time.GetUtcNow() + TimeSpan.FromSeconds(_o.BreakSeconds);
            if (probe) _probeInFlight = false;
            opened = wasClosed && _openUntil is not null;
        }

        if (opened)
        {
            L2CacheMetrics.CircuitOpenedTotal.Add(1);
            logger.LogWarning(ex,
                "L2 cache (Valkey) {Op} failed or exceeded {TimeoutMs} ms; skipping L2 for {Seconds} s (requests use L1 and the database).",
                op, _o.OperationTimeoutMs, _o.BreakSeconds);
        }
        else
        {
            logger.LogDebug(ex, "L2 cache {Op} failed.", op);
        }
    }
}
