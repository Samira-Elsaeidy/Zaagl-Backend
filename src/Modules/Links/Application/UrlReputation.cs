using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Modules.Links.Application;

/// <summary><c>Checked</c> is false for every fail-open path (outage, timeout, budget exhausted): the
/// caller must leave the link's <c>SafetyCheckedAt</c> null so the re-check job picks it up later,
/// rather than recording a check that never actually happened (spec §7.15).</summary>
public sealed record ReputationResult(bool IsSafe, string? Threat = null, bool Checked = true);

public interface IUrlReputationChecker
{
    Task<ReputationResult> CheckAsync(string url, CancellationToken cancellationToken);
}

public sealed class WebRiskOptions
{
    public const string Section = "WebRisk";

    /// <summary>Google Web Risk API key. When empty, scanning is skipped.</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class SafetyOptions
{
    public const string Section = "Safety";

    /// <summary>Spec §7.15: the provider's free tier; a product decision (v9.4), not a technical limit.</summary>
    public int MonthlyLookupBudget { get; set; } = 100_000;

    public int TimeoutMs { get; set; } = 400;

    /// <summary>Spec §7.6: seeds the destination blocklist with known public shorteners/open redirectors
    /// so links cannot be chained to hide a destination.</summary>
    public string[] DenyRedirectorHosts { get; set; } = [];
}

/// <summary>
/// Spec §7.15: Google Web Risk lookup, cached per normalised URL for 24h, gated by a shared monthly
/// budget (one row per month, incremented atomically so N instances share it), with a 400 ms timeout.
/// Fails open on timeout, error or budget exhaustion - a link is still created/kept, just with
/// SafetyCheckedAt left null for the re-check job to retry once there's budget or the provider recovers.
/// The 24h cache is in-memory per instance (year-1 single-instance topology, same justification as the
/// guest rate limiter): a cache miss on another instance just re-spends one budget unit, never incorrect.
/// </summary>
public sealed class WebRiskChecker(
    HttpClient http, IOptions<WebRiskOptions> webRisk, IOptions<SafetyOptions> safety,
    LinksDbContext db, ILogger<WebRiskChecker> logger, TimeProvider time)
    : IUrlReputationChecker
{
    private const string Endpoint = "https://webrisk.googleapis.com/v1/uris:search";
    private readonly string _apiKey = webRisk.Value.ApiKey;
    private readonly ConcurrentDictionary<string, (ReputationResult Result, DateTime ExpiresAt)> _cache = new();

    public async Task<ReputationResult> CheckAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return new(true); // scanning disabled (no key configured)

        var now = time.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue(url, out var entry) && entry.ExpiresAt > now) return entry.Result;

        if (!await TryReserveBudgetAsync(now, cancellationToken))
        {
            logger.LogWarning("Web Risk monthly lookup budget exhausted; skipping lookup (fail-open)");
            return new(true, Checked: false);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(safety.Value.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var result = await LookupAsync(url, linked.Token);
            _cache[url] = (result, now.AddHours(24));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Web Risk lookup failed or timed out; fail-open");
            return new(true, Checked: false);
        }
    }

    private async Task<ReputationResult> LookupAsync(string url, CancellationToken ct)
    {
        var query = "threatTypes=MALWARE&threatTypes=SOCIAL_ENGINEERING&threatTypes=UNWANTED_SOFTWARE"
            + $"&uri={Uri.EscapeDataString(url)}&key={Uri.EscapeDataString(_apiKey)}";
        using var response = await http.GetAsync($"{Endpoint}?{query}", ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("threat", out var threat)
            && threat.TryGetProperty("threatTypes", out var types)
            && types.GetArrayLength() > 0)
        {
            return new(false, types[0].GetString());
        }
        return new(true);
    }

    /// <summary>Atomically reserves one unit of this month's shared budget. Returns false (no unit spent)
    /// once the month's budget is used up.</summary>
    private async Task<bool> TryReserveBudgetAsync(DateTime now, CancellationToken ct)
    {
        var month = new DateOnly(now.Year, now.Month, 1);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO links.safety_budget (month, used) VALUES ({month}, 0) ON CONFLICT (month) DO NOTHING", ct);
        var reserved = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE links.safety_budget SET used = used + 1 WHERE month = {month} AND used < {safety.Value.MonthlyLookupBudget}", ct);
        return reserved > 0;
    }
}
