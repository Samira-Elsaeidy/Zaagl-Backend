using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Api.Infrastructure;

/// <summary>Per-minute permits of the named ASP.NET rate-limit policies, all partitioned by
/// <see cref="IpPartition"/>. Auth is 10/min per spec §7.4; the others keep the Phase 0 values.</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int AuthPerMinute { get; set; } = 10;
    /// <summary>ARC-014 §2 device flow (technical defaults): code issue per IP, token polls per IP, activation per user.</summary>
    public int DeviceCodePerMinute { get; set; } = 10;
    public int DeviceTokenPerMinute { get; set; } = 30;
    public int DeviceActivatePerMinute { get; set; } = 10;
    public int LinkWritePerMinute { get; set; } = 60;
    public int UnlockPerMinute { get; set; } = 10;
    /// <summary>ARC-003 §4.1: descriptor reads per IP and code (ARC-013 OPEN, technical default 120).</summary>
    public int ContentReadPerMinute { get; set; } = 120;
    /// <summary>Content create/edit per principal (ARC-013 OPEN, technical default 60).</summary>
    public int ContentWritePerMinute { get; set; } = 60;
}

/// <summary>Spec §7.3 redirect limits (BE-031), per minute. Defaults are the spec's numbers.</summary>
public sealed class RedirectRateLimitOptions
{
    public const string Section = "RateLimits:Redirects";

    public bool Enabled { get; set; } = true;

    /// <summary>Every redirect request (GET and HEAD alike) per IP partition (IPv4 address, IPv6 /64).</summary>
    public int PerMinute { get; set; } = 300;

    /// <summary>Misses (404/410) per IPv6 /64 or IPv4 address.</summary>
    public int MissesPerMinute { get; set; } = 30;

    /// <summary>Misses per IPv6 /56 (no IPv4 counterpart).</summary>
    public int MissesPerMinutePer56 { get; set; } = 120;

    /// <summary>Misses per IPv6 /48 or IPv4 /24.</summary>
    public int MissesPerMinutePer48 { get; set; } = 300;
}

public readonly record struct RedirectLimitDecision(bool Allowed, TimeSpan RetryAfter)
{
    public static readonly RedirectLimitDecision Allow = new(true, TimeSpan.Zero);
}

/// <summary>
/// Spec §7.3: the redirect limiter sits behind an interface so a shared (Valkey) implementation can replace
/// this in-memory, per-instance one when the API scales out.
/// </summary>
public interface IRedirectRateLimiter
{
    /// <summary>Called before the lookup: counts the request, and refuses it when the partition is over the
    /// redirect limit or has already spent any of its miss budgets for this minute.</summary>
    RedirectLimitDecision TryAcquire(IPAddress? address);

    /// <summary>Called after a lookup that ended in 404 or 410.</summary>
    void RecordMiss(IPAddress? address);
}

/// <summary>
/// Fixed one-minute windows keyed by IP partition (<see cref="IpPartition"/>). A partition that spent its miss
/// budget gets <c>429</c> for every redirect until the window ends, hits included, so an enumerator learns
/// nothing more than its budget allows. Memory is bounded: buckets of past windows are dropped once per window.
/// </summary>
public sealed class InMemoryRedirectRateLimiter(TimeProvider time, IOptionsMonitor<RedirectRateLimitOptions> options)
    : IRedirectRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (long Window, int Count)> _counts = new(StringComparer.Ordinal);
    private long _lastSweptWindow = long.MinValue;

    public RedirectLimitDecision TryAcquire(IPAddress? address)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) return RedirectLimitDecision.Allow;

        var now = time.GetUtcNow();
        var window = CurrentWindow(now);
        Sweep(window);

        foreach (var (key, limit) in MissTiers(address, o))
        {
            if (Peek(key, window) >= limit) return Deny(now);
        }

        return Increment("r|" + IpPartition.Key(address), window) > o.PerMinute ? Deny(now) : RedirectLimitDecision.Allow;
    }

    public void RecordMiss(IPAddress? address)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) return;

        var window = CurrentWindow(time.GetUtcNow());
        foreach (var (key, _) in MissTiers(address, o))
            Increment(key, window);
    }

    private static IEnumerable<(string Key, int Limit)> MissTiers(IPAddress? address, RedirectRateLimitOptions o)
    {
        yield return ("m|" + IpPartition.Key(address), o.MissesPerMinute);
        if (IpPartition.WidePrefixKey(address, 56) is { } p56) yield return ("m|" + p56, o.MissesPerMinutePer56);
        if (IpPartition.WidePrefixKey(address, 48) is { } p48) yield return ("m|" + p48, o.MissesPerMinutePer48);
        if (IpPartition.V4Prefix24Key(address) is { } p24) yield return ("m|" + p24, o.MissesPerMinutePer48);
    }

    private static long CurrentWindow(DateTimeOffset now) => now.ToUnixTimeSeconds() / (long)Window.TotalSeconds;

    private static RedirectLimitDecision Deny(DateTimeOffset now)
    {
        var seconds = (long)Window.TotalSeconds;
        return new(false, TimeSpan.FromSeconds(seconds - now.ToUnixTimeSeconds() % seconds));
    }

    private int Peek(string key, long window) =>
        _counts.TryGetValue(key, out var current) && current.Window == window ? current.Count : 0;

    private int Increment(string key, long window)
    {
        while (true)
        {
            if (!_counts.TryGetValue(key, out var current))
            {
                if (_counts.TryAdd(key, (window, 1))) return 1;
                continue;
            }
            var next = current.Window == window ? (window, current.Count + 1) : (window, 1);
            if (_counts.TryUpdate(key, next, current)) return next.Item2;
        }
    }

    private void Sweep(long window)
    {
        var last = Interlocked.Read(ref _lastSweptWindow);
        if (last >= window || Interlocked.CompareExchange(ref _lastSweptWindow, window, last) != last) return;

        foreach (var entry in _counts)
        {
            if (entry.Value.Window < window)
                _counts.TryRemove(entry);
        }
    }
}
