using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Modules.Links.Application;

public sealed class GuestOptions
{
    public const string Section = "Guests";

    public bool Enabled { get; set; } = true;

    /// <summary>Spec §7.12: 20 per rolling 24h per creator key (aliases included).</summary>
    public int LinksPerDay { get; set; } = 20;

    /// <summary>Wider IPv6 ceilings so one /56 or /48 allocation can't multiply its /64 quota.</summary>
    public int LinksPerDayPer56 { get; set; } = 100;
    public int LinksPerDayPer48 { get; set; } = 300;

    public int PerMinute { get; set; } = 5;

    /// <summary>In-memory, per-instance brake against distributed abuse (spec §7.12).</summary>
    public int GlobalPerHour { get; set; } = 5000;

    public int ExpiryDays { get; set; } = 7;
}

/// <summary>
/// Spec §7.12: guests are identified only by their IP partition, never by a stored IP. 32 random bytes
/// from <c>GUEST_KEY</c>; changing it only resets guest quotas, it never affects already-issued links.
/// </summary>
public sealed class GuestKeyOptions
{
    public const string Section = "";
    public string Key { get; set; } = "";
}

public static class GuestIdentity
{
    /// <summary>HMAC-SHA256(GUEST_KEY, tag), truncated to 16 bytes. Used both for the link's stored
    /// <c>guest_creator_key</c> (and idempotency scope) and for each quota bucket's <c>prefix_key</c>.</summary>
    public static byte[] DeriveKey(byte[] guestKey, string tag) =>
        HMACSHA256.HashData(guestKey, Encoding.UTF8.GetBytes(tag))[..16];

    public static byte[] ResolveGuestKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Dev-only fallback, matching ShortLinks:ScrambleKey's pattern: guest quotas simply reset
            // every restart when unconfigured, which never affects any already-issued link.
            return RandomNumberGenerator.GetBytes(32);
        }
        try
        {
            return Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(configured);
        }
    }
}

/// <summary>Holds the resolved GUEST_KEY bytes for the process lifetime (same shape as CodeGenerator's
/// scramble key: resolved once at startup, never re-read).</summary>
public sealed class GuestKeyProvider(byte[] key)
{
    public byte[] Key { get; } = key;
}

public sealed record GuestQuotaCheck(string Tag, byte[] PrefixKey, int Limit);

/// <summary>
/// Spec §7.12: each applicable prefix (creator key, and for IPv6 also /56 and /48) is checked and
/// incremented inside the creation transaction, in hourly buckets summed over the last 24h, locked in
/// key order so concurrent requests serialise instead of deadlocking.
/// </summary>
public static class GuestQuotaStore
{
    public static async Task<string?> CheckAndIncrementAllAsync(
        LinksDbContext db, IReadOnlyList<GuestQuotaCheck> checks, DateTime now, CancellationToken ct)
    {
        var ordered = checks.OrderBy(c => Convert.ToHexString(c.PrefixKey), StringComparer.Ordinal).ToList();
        foreach (var check in ordered)
        {
            if (!await CheckAndIncrementAsync(db, check.PrefixKey, check.Limit, now, ct))
                return check.Tag;
        }
        return null;
    }

    private static async Task<bool> CheckAndIncrementAsync(
        LinksDbContext db, byte[] prefixKey, int limit, DateTime now, CancellationToken ct)
    {
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var since = now.AddHours(-24);

        // Create-or-lock the current hour's bucket, then lock every bucket in the last 24h window.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO links.guest_quota (prefix_key, hour, count) VALUES ({prefixKey}, {hour}, 0)
             ON CONFLICT (prefix_key, hour) DO UPDATE SET count = guest_quota.count
             """, ct);

        // FOR UPDATE cannot be combined with an aggregate directly; lock the window's rows in a subquery,
        // then sum the locked rows in the outer query.
        var sum = await db.Database.SqlQuery<long?>(
                $"""
                 SELECT COALESCE(SUM(locked.count), 0)::bigint AS "Value" FROM (
                     SELECT count FROM links.guest_quota
                     WHERE prefix_key = {prefixKey} AND hour > {since}
                     FOR UPDATE
                 ) AS locked
                 """)
            .FirstAsync(ct) ?? 0;

        if (sum >= limit) return false;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE links.guest_quota SET count = count + 1 WHERE prefix_key = {prefixKey} AND hour = {hour}", ct);
        return true;
    }
}

/// <summary>Spec §7.12: buckets older than 25h (one more than the 24h window ever reads) are deleted hourly.</summary>
public sealed class GuestQuotaCleanupJob(IServiceScopeFactory scopes, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
                var cutoff = clock.GetUtcNow().UtcDateTime.AddHours(-25);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM links.guest_quota WHERE hour < {cutoff}", stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>
/// Spec §7.12: the per-minute token bucket and the global hourly ceiling are explicitly "counted in
/// memory per instance" (not a shared-state guarantee like the 24h quota above), matching year-1's
/// single-instance topology.
/// </summary>
public sealed class GuestRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (long WindowMinute, int Count)> _perMinute = new();
    private readonly ConcurrentDictionary<(string Partition, long Hour), int> _perPartitionHourly = new();
    private readonly ConcurrentDictionary<long, int> _globalHourly = new();

    public bool TryPerMinute(string partition, int limit)
    {
        var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        while (true)
        {
            var current = _perMinute.GetOrAdd(partition, _ => (minute, 0));
            if (current.WindowMinute != minute)
            {
                if (_perMinute.TryUpdate(partition, (minute, 1), current)) return true;
                continue;
            }
            if (current.Count >= limit) return false;
            if (_perMinute.TryUpdate(partition, (minute, current.Count + 1), current)) return true;
        }
    }

    /// <summary>When the global ceiling trips, the partitions that created the most in the last hour are
    /// paused first (spec §7.12), so one burst does not pause guests everywhere.</summary>
    public bool TryGlobalCeiling(string partition, int globalLimit)
    {
        var hour = time.GetUtcNow().ToUnixTimeSeconds() / 3600;
        var globalCount = _globalHourly.AddOrUpdate(hour, 1, (_, c) => c + 1);
        var partitionCount = _perPartitionHourly.AddOrUpdate((partition, hour), 1, (_, c) => c + 1);

        if (globalCount <= globalLimit) return true;

        var activePartitions = Math.Max(1, _perPartitionHourly.Keys.Count(k => k.Hour == hour));
        var fairShare = Math.Max(1, globalLimit / activePartitions);
        return partitionCount <= fairShare;
    }
}
