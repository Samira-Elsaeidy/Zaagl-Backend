using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

/// <summary>Spec §7.15: host, host-suffix or url-prefix. Checked in Phase B and in PATCH, ahead of the
/// (slower, budget-limited) reputation lookup - a known-bad host never spends a safety-check budget unit.</summary>
public interface IDestinationBlocklist
{
    Task<bool> IsBlockedAsync(string targetHost, string normalizedUrl, CancellationToken ct);
}

/// <summary>Spec §4.5 rule 5: an admin-managed denylist layered on top of SlugRules' static reserved
/// words - patterns added after the fact (e.g. a brand name someone keeps squatting).</summary>
public interface IAliasDenylist
{
    Task<bool> IsDeniedAsync(string slugKey, CancellationToken ct);
}

public sealed class AliasDenylist(LinksDbContext db) : IAliasDenylist
{
    public async Task<bool> IsDeniedAsync(string slugKey, CancellationToken ct)
    {
        var entries = await db.AliasDenylist.AsNoTracking().ToListAsync(ct);
        return entries.Any(e => e.Kind == "prefix"
            ? slugKey.StartsWith(e.Pattern, StringComparison.OrdinalIgnoreCase)
            : string.Equals(slugKey, e.Pattern, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DestinationBlocklist(LinksDbContext db, IOptions<SafetyOptions> safety) : IDestinationBlocklist
{
    public async Task<bool> IsBlockedAsync(string targetHost, string normalizedUrl, CancellationToken ct)
    {
        if (safety.Value.DenyRedirectorHosts.Contains(targetHost, StringComparer.OrdinalIgnoreCase))
            return true;

        var entries = await db.BlockedDestinations.AsNoTracking().ToListAsync(ct);
        foreach (var entry in entries)
        {
            var match = entry.Kind switch
            {
                BlockedDestinationKinds.Host => string.Equals(targetHost, entry.Pattern, StringComparison.OrdinalIgnoreCase),
                BlockedDestinationKinds.HostSuffix => targetHost.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase)
                    || targetHost.EndsWith("." + entry.Pattern, StringComparison.OrdinalIgnoreCase),
                BlockedDestinationKinds.UrlPrefix => normalizedUrl.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (match) return true;
        }
        return false;
    }
}

/// <summary>Spec §7.11: while an alias freeze is active, no new alias can be claimed by anyone (generated
/// links, renewals, deletes and redirects are unaffected). Cached 60s per the spec's own text.</summary>
public sealed class AliasFreezeChecker(LinksDbContext db, HybridCache cache, TimeProvider time)
{
    public async Task<AliasFreeze?> GetActiveFreezeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return await cache.GetOrCreateAsync(
            "alias-freeze:active",
            async innerCt => await db.AliasFreezes.AsNoTracking()
                .Where(f => f.EndedAt == null && f.StartsAt <= now && now < f.Until)
                .OrderByDescending(f => f.Until)
                .FirstOrDefaultAsync(innerCt),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
            cancellationToken: ct);
    }
}

public sealed class AbuseOptions
{
    public const string Section = "Abuse";
    public int EvidenceRetentionDays { get; set; } = 90;
}

/// <summary>
/// Spec §7.15's takedown routine: used by the safety re-check job below (kind "safety") and, once an
/// admin API exists, by admin disable/bulk-takedown (kind "abuse") - the mechanism is the same either way.
/// </summary>
public sealed class TakedownService(LinksDbContext db, ILinkCacheInvalidator invalidator, IOptions<AbuseOptions> options, TimeProvider time)
{
    public async Task TakeDownAsync(Link link, string kind, string? reason, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        link.DisabledAt ??= now;
        link.DisabledKind = kind; // overwrites admin/user-disabled, so a user re-enable can't undo a takedown
        link.DisabledReason = reason;

        if (link.IsCustom && !await db.BlockedAliases.AnyAsync(a => a.SlugKey == link.SlugKey, ct))
        {
            db.BlockedAliases.Add(new BlockedAlias
            {
                SlugKey = link.SlugKey,
                Reason = reason ?? kind,
            });
        }

        db.AbuseEvidence.Add(new AbuseEvidence
        {
            LinkId = link.Id,
            Slug = link.Slug,
            TargetUrl = link.DestinationUrl,
            DisabledKind = kind,
            Reason = reason,
            KeepUntil = now.AddDays(options.Value.EvidenceRetentionDays),
        });

        await db.SaveChangesAsync(ct);
        await invalidator.NotifyChangedAsync([link.SlugKey], ct);
    }
}

/// <summary>
/// Spec §7.15: every 6h, re-checks links that are new, recently clicked, or never checked, within the
/// shared monthly budget. Claims a batch under a short lock (so concurrent instances split the work),
/// then looks up outside any transaction - an unsafe verdict takes the link down via TakedownService.
/// </summary>
public sealed class SafetyRecheckJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SafetyRecheckJob> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
        var reputation = scope.ServiceProvider.GetRequiredService<IUrlReputationChecker>();
        var takedowns = scope.ServiceProvider.GetRequiredService<TakedownService>();

        var now = clock.GetUtcNow().UtcDateTime;
        var staleSince = now.AddHours(-6);
        var recentlyCreated = now.AddDays(-30);

        List<Guid> ids;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            ids = await db.Database.SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM links.links
                WHERE deleted_at IS NULL AND disabled_at IS NULL
                  AND (purge_after IS NULL OR purge_after > {now})
                  AND (safety_checked_at IS NULL OR safety_checked_at < {staleSince} OR created_at > {recentlyCreated})
                ORDER BY safety_checked_at NULLS FIRST
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);

            if (ids.Count > 0)
                await db.Links.Where(l => ids.Contains(l.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.SafetyCheckedAt, now), ct);

            await tx.CommitAsync(ct);
        }

        foreach (var id in ids)
        {
            var link = await db.Links.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (link is null) continue;

            var result = await reputation.CheckAsync(link.DestinationUrl, ct);
            if (!result.Checked)
            {
                // Fail-open: undo the "checked" stamp so this link is retried next run.
                await db.Links.Where(l => l.Id == id).ExecuteUpdateAsync(s => s.SetProperty(l => l.SafetyCheckedAt, (DateTime?)null), ct);
                continue;
            }
            if (!result.IsSafe)
            {
                await takedowns.TakeDownAsync(link, DisabledKinds.Safety, $"Flagged by re-check: {result.Threat}", ct);
                logger.LogWarning("Disabled link {LinkId} after an unsafe re-check verdict ({Threat})", link.Id, result.Threat);
            }
        }
    }
}

/// <summary>Spec §7.15: <c>POST /api/reports</c> (public, CAPTCHA-gated) plus the admin list/resolve side.</summary>
public sealed class ReportService(LinksDbContext db, TimeProvider time)
{
    public async Task<AbuseReport> CreateAsync(string? code, string? url, string category, string? details, string? reporterEmail, CancellationToken ct)
    {
        var report = new AbuseReport
        {
            Code = code,
            Url = url,
            Category = category,
            Details = details,
            ReporterEmail = reporterEmail,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.AbuseReports.Add(report);
        await db.SaveChangesAsync(ct);
        return report;
    }

    public async Task<(IReadOnlyList<AbuseReport> Items, int Total)> ListAsync(string? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.AbuseReports.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<bool> ResolveAsync(Guid id, string status, string? resolution, CancellationToken ct)
    {
        var report = await db.AbuseReports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null) return false;

        report.Status = status;
        report.Resolution = resolution;
        report.ResolvedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

// ---- CAPTCHA (spec §7.12, §7.15): shared seam for guest creation, registration, resend-confirmation,
// forgot-password and abuse reports. Wired into guest creation now; the Identity-side call sites are a
// separate phase (see docs/TRACEABILITY.md).

public sealed class CaptchaOptions
{
    public const string Section = "Captcha";
    public bool Enabled { get; set; } = true;
    public string SiteKey { get; set; } = "";
    public string Secret { get; set; } = "";
}

public interface ICaptchaVerifier
{
    /// <summary>Null token -> <c>captcha-required</c>; a failed verification -> <c>captcha-invalid</c>;
    /// the verifier itself erroring -> <c>service_unavailable</c> (never a silent pass, spec §7.12).</summary>
    Task<Result<bool>> VerifyAsync(string? token, string expectedAction, string expectedHostname, CancellationToken ct);
}

public sealed class NoOpCaptchaVerifier : ICaptchaVerifier
{
    public Task<Result<bool>> VerifyAsync(string? token, string expectedAction, string expectedHostname, CancellationToken ct) =>
        Task.FromResult(Result<bool>.Ok(true));
}

public sealed class TurnstileVerifier(HttpClient http, IOptions<CaptchaOptions> options, ILogger<TurnstileVerifier> logger) : ICaptchaVerifier
{
    private const string Endpoint = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private sealed record SiteverifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("challenge_ts")] DateTimeOffset? ChallengeTimestamp);

    public async Task<Result<bool>> VerifyAsync(string? token, string expectedAction, string expectedHostname, CancellationToken ct)
    {
        if (!options.Value.Enabled) return Result<bool>.Ok(true);
        if (string.IsNullOrWhiteSpace(token)) return Error.Validation("captcha-required");

        try
        {
            using var response = await http.PostAsJsonAsync(Endpoint,
                new { secret = options.Value.Secret, response = token }, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<SiteverifyResponse>(ct);

            if (body is null || !body.Success) return Error.Validation("captcha-invalid");

            // Cloudflare's own documented sandbox secrets (spec §7.12: "test and development environments
            // use Turnstile's documented test keys") return a canned response with no real action/hostname
            // to echo back and a fixed challenge_ts - only `success` is meaningful for them.
            if (!IsDocumentedTestSecret(options.Value.Secret))
            {
                if (!string.Equals(body.Action, expectedAction, StringComparison.Ordinal)) return Error.Validation("captcha-invalid");
                if (!string.Equals(body.Hostname, expectedHostname, StringComparison.OrdinalIgnoreCase)) return Error.Validation("captcha-invalid");
                if (body.ChallengeTimestamp is { } ts && (DateTimeOffset.UtcNow - ts) > TimeSpan.FromSeconds(300)) return Error.Validation("captcha-invalid");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogError(ex, "Turnstile siteverify call failed");
            return Error.ServiceUnavailable("Could not verify the CAPTCHA right now. Try again shortly.");
        }
    }

    private static readonly string[] DocumentedTestSecrets =
    [
        "1x0000000000000000000000000000000AA", // always passes
        "2x0000000000000000000000000000000AA", // always fails
        "3x0000000000000000000000000000000AA", // always errors
    ];

    private static bool IsDocumentedTestSecret(string secret) => DocumentedTestSecrets.Contains(secret);
}
