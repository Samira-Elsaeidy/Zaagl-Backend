using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.DeveloperTools.Contracts;
using Zaagl.Modules.DeveloperTools.Domain;
using Zaagl.Modules.DeveloperTools.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.DeveloperTools.Application;

/// <summary>Configuration section <c>Hooks</c> (docs/phase2/webhook-catcher.md). Technical defaults, not product decisions.</summary>
public sealed class HookOptions
{
    public const string Section = "Hooks";

    /// <summary>After expiry the owner can still read the captures this long; then the sweep purges them.</summary>
    public int ReadOnlyGraceHours { get; set; } = 24;

    /// <summary>The server closes a live stream after this long (clients reconnect with Last-Event-ID).</summary>
    public int StreamMaxMinutes { get; set; } = 10;

    /// <summary>Open live streams per user (per API instance).</summary>
    public int MaxStreamsPerUser { get; set; } = 3;

    /// <summary>Public base of the ingest URL (the short host). The host never comes from the request.</summary>
    public string IngestBaseUrl { get; set; } = "";
}

/// <summary>One request as the ingest endpoint read it (already bounded and sanitised by <see cref="HookCapture"/>).</summary>
public sealed record CapturedRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyList<string[]> Headers,
    string? ContentType,
    byte[] Body,
    long BodyBytesTotal,
    bool BodyTruncated,
    string SourceFingerprint);

public enum IngestOutcome
{
    Stored,
    NotFound,
    RateLimited,
    Busy,
}

public sealed record IngestResult(IngestOutcome Outcome, int ResponseStatus = 404, int RetryAfterSeconds = 0);

/// <summary>Pure helpers that bound and sanitise what ingest stores.</summary>
public static class HookCapture
{
    public const int MaxMethodLength = 16;
    public const int MaxPathLength = 2048;
    public const int MaxQueryLength = 8192;
    public const int MaxHeaders = 100;
    public const int MaxHeaderValueLength = 8192;

    /// <summary>Hop-by-hop headers (RFC 9110 §7.6.1) and proxy headers that reveal the sender's address: never stored.</summary>
    public static readonly IReadOnlySet<string> DroppedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port", "X-Real-IP", "Forwarded",
        "CF-Connecting-IP", "CF-Connecting-IPv6", "True-Client-IP", "CF-Ray", "CF-IPCountry", "CF-Visitor", "CDN-Loop",
        "X-Test-Remote-Ip",
    };

    public static bool IsValidToken(string? token) =>
        token is { Length: 22 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>PostgreSQL text and jsonb cannot hold NUL: replaced by U+FFFD, and the value is cut to <paramref name="max"/>.</summary>
    public static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cleaned = value.Contains('\0') ? value.Replace('\0', '�') : value;
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }

    public static IReadOnlyList<string[]> Headers(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Where(h => !DroppedHeaders.Contains(h.Key))
            .Take(MaxHeaders)
            .Select(h => new[] { Clean(h.Key, 256), Clean(h.Value, MaxHeaderValueLength) })
            .ToList();

    /// <summary>Valid UTF-8 without control characters other than tab, CR and LF, else null.</summary>
    public static string? AsText(byte[]? body)
    {
        if (body is null || body.Length == 0) return body is null ? null : "";
        try
        {
            var text = new UTF8Encoding(false, true).GetString(body);
            return text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')) ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>
/// E2.1 webhook catcher (docs/phase2/webhook-catcher.md): owner management, ingest and the live-stream poll. Owner routes are
/// owner only (404 otherwise). Ingest holds the catcher row lock for one short transaction: the per-minute window, the insert
/// and the trimming of the oldest captures (count and bytes) are atomic, so the caps hold under any concurrency and on any
/// number of API instances.
/// </summary>
public sealed class HookService(DevToolsDbContext db, IContentPlanProvider plans, TimeProvider time, IOptions<HookOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxLabelLength = 100;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public string IngestUrl(string token, string fallbackBase) =>
        $"{(string.IsNullOrWhiteSpace(options.Value.IngestBaseUrl) ? fallbackBase : options.Value.IngestBaseUrl).TrimEnd('/')}/hook/{token}";

    // ---------------------------------------------------------------------------------------------------
    // Owner management
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<HookRow>> CreateAsync(Guid userId, CreateHookCommand command, CancellationToken ct)
    {
        var tools = (await plans.GetForUserAsync(userId, ct)).ToolsOrNone;
        if (tools.HookMaxActive < 1 || tools.HookLifetimeHours < 1 || tools.HookMaxRequests < 1)
            return Error.PlanRequired("Webhook catchers are not available on your plan.");

        var label = CleanLabel(command.Label, out var labelError);
        if (labelError is not null) return labelError;
        var status = command.ResponseStatus ?? 200;
        if (!IsAllowedStatus(status)) return Error.Validation("responseStatus must be 200-299 or 400-599 (no redirects).");
        var hours = command.LifetimeHours ?? tools.HookLifetimeHours;
        if (hours < 1) return Error.Validation("lifetimeHours must be at least 1.");
        if (hours > tools.HookLifetimeHours) return Error.PlanRequired($"Your plan allows catchers of at most {tools.HookLifetimeHours} hours.");

        var now = Now;
        var expires = now.AddHours(hours);
        var row = new HookRow
        {
            Id = Guid.CreateVersion7(), OwnerId = userId, Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            Label = label, ResponseStatus = (short)status, CreatedAt = now, ExpiresAt = expires,
            PurgeAfter = expires.AddHours(Math.Max(0, options.Value.ReadOnlyGraceHours)),
            MaxRequests = tools.HookMaxRequests, MaxBodyBytes = tools.HookMaxBodyBytes,
            MaxStoredBytes = tools.HookMaxStoredBytes, IngestPerMinute = Math.Max(1, tools.HookIngestPerMinute),
        };

        // The live-catcher cap under a transaction-scoped advisory lock per owner: two parallel creates cannot both pass the count.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({OwnerLockKey(userId)})", ct);
        var live = await db.Hooks.CountAsync(h => h.OwnerId == userId && h.ExpiresAt > now, ct);
        if (live >= tools.HookMaxActive)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return Error.Of(ErrorCodes.LimitReached, $"Your plan allows {tools.HookMaxActive} live catcher(s) at a time. Delete one or wait for it to expire.");
        }
        db.Hooks.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return row;
    }

    public async Task<IReadOnlyList<HookRow>> ListAsync(Guid userId, CancellationToken ct)
    {
        var now = Now;
        return await db.Hooks.AsNoTracking().Where(h => h.OwnerId == userId && h.PurgeAfter > now)
            .OrderByDescending(h => h.CreatedAt).Take(200).ToListAsync(ct);
    }

    /// <summary>Owner only; purgeable catchers are invisible (404) even before the sweep removes them.</summary>
    public async Task<Result<HookRow>> GetOwnedAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var now = Now;
        var row = await db.Hooks.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.OwnerId == userId && h.PurgeAfter > now, ct);
        return row is null ? Error.NotFound() : row;
    }

    public async Task<Result<HookRow>> UpdateAsync(Guid id, Guid userId, UpdateHookCommand command, CancellationToken ct)
    {
        var owned = await GetOwnedAsync(id, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        string? label = owned.Value!.Label;
        if (command.LabelSet)
        {
            label = CleanLabel(command.Label, out var labelError);
            if (labelError is not null) return labelError;
        }
        var status = command.ResponseStatus ?? owned.Value.ResponseStatus;
        if (!IsAllowedStatus(status)) return Error.Validation("responseStatus must be 200-299 or 400-599 (no redirects).");
        await db.Hooks.Where(h => h.Id == id && h.OwnerId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Label, label).SetProperty(h => h.ResponseStatus, (short)status), ct);
        return await GetOwnedAsync(id, userId, ct);
    }

    /// <summary>Deletes the catcher and (cascade) every capture at once.</summary>
    public async Task<Result<bool>> DeleteAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var owned = await GetOwnedAsync(id, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        await db.Hooks.Where(h => h.Id == id && h.OwnerId == userId).ExecuteDeleteAsync(ct);
        return true;
    }

    public async Task<Result<HookRequestPage>> ListRequestsAsync(Guid id, Guid userId, long? before, int limit, CancellationToken ct)
    {
        var owned = await GetOwnedAsync(id, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        limit = Math.Clamp(limit, 1, 100);
        var query = db.HookRequests.AsNoTracking().Where(r => r.HookId == id);
        if (before is { } b) query = query.Where(r => r.Id < b);
        var rows = await query.OrderByDescending(r => r.Id).Take(limit + 1).Select(SummaryProjection).ToListAsync(ct);
        var more = rows.Count > limit;
        var items = rows.Take(limit).Select(Utc).ToList();
        return new HookRequestPage(items, more ? items[^1].Id : null);
    }

    /// <summary>The live stream's poll: captures newer than <paramref name="afterId"/>, oldest first.</summary>
    public async Task<IReadOnlyList<HookRequestSummaryDto>> RequestsAfterAsync(Guid id, long afterId, CancellationToken ct) =>
        (await db.HookRequests.AsNoTracking().Where(r => r.HookId == id && r.Id > afterId)
            .OrderBy(r => r.Id).Take(50).Select(SummaryProjection).ToListAsync(ct)).Select(Utc).ToList();

    public async Task<long> LatestRequestIdAsync(Guid id, CancellationToken ct) =>
        await db.HookRequests.AsNoTracking().Where(r => r.HookId == id).MaxAsync(r => (long?)r.Id, ct) ?? 0;

    public async Task<Result<HookRequestDto>> GetRequestAsync(Guid id, Guid userId, long requestId, CancellationToken ct)
    {
        var owned = await GetOwnedAsync(id, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var r = await db.HookRequests.AsNoTracking().FirstOrDefaultAsync(x => x.HookId == id && x.Id == requestId, ct);
        if (r is null) return Error.NotFound();
        var headers = JsonSerializer.Deserialize<List<string[]>>(r.Headers, Json) ?? [];
        return new HookRequestDto(r.Id, DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc), r.Method, r.Path, r.Query, headers,
            r.ContentType, r.BodyBytesTotal, r.BodyTruncated, r.SourceFingerprint,
            r.Body is null ? null : Convert.ToBase64String(r.Body), HookCapture.AsText(r.Body));
    }

    public async Task<Result<bool>> DeleteRequestAsync(Guid id, Guid userId, long? requestId, CancellationToken ct)
    {
        var owned = await GetOwnedAsync(id, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Hooks.FromSqlInterpolated($"SELECT * FROM devtools.hooks WHERE id = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct);
        var deleted = requestId is { } rid
            ? await db.HookRequests.Where(r => r.HookId == id && r.Id == rid).ExecuteDeleteAsync(ct)
            : await db.HookRequests.Where(r => r.HookId == id).ExecuteDeleteAsync(ct);
        if (requestId is not null && deleted == 0) { await tx.RollbackAsync(CancellationToken.None); return Error.NotFound(); }
        await RefreshCountersAsync(id, null, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // Ingest (zaa.gl/hook/{token})
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The fast pre-check before the body is read: a live catcher with this exact (case-sensitive) token, or null.</summary>
    public async Task<HookRow?> FindLiveAsync(string token, CancellationToken ct)
    {
        if (!HookCapture.IsValidToken(token)) return null;
        var now = Now;
        return await db.Hooks.AsNoTracking().FirstOrDefaultAsync(h => h.Token == token && h.ExpiresAt > now, ct);
    }

    public async Task<IngestResult> IngestAsync(Guid hookId, CapturedRequest request, CancellationToken ct)
    {
        var now = Now;
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        var headersJson = JsonSerializer.Serialize(request.Headers, Json);
        var stored = request.Body.Length + Encoding.UTF8.GetByteCount(headersJson) + request.Path.Length + request.Query.Length;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            var hook = (await db.Hooks.FromSqlInterpolated($"SELECT * FROM devtools.hooks WHERE id = {hookId} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
            if (hook is null || hook.ExpiresAt <= now) { await tx.RollbackAsync(CancellationToken.None); return new(IngestOutcome.NotFound); }

            var sameWindow = hook.WindowStart == minute;
            if (sameWindow && hook.WindowCount >= hook.IngestPerMinute)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return new(IngestOutcome.RateLimited, 429, Math.Max(1, 60 - now.Second));
            }

            db.HookRequests.Add(new HookRequestRow
            {
                HookId = hookId, ReceivedAt = now, Method = request.Method, Path = request.Path, Query = request.Query,
                Headers = headersJson, ContentType = request.ContentType, Body = request.Body.Length == 0 ? null : request.Body,
                BodyBytesTotal = request.BodyBytesTotal, BodyTruncated = request.BodyTruncated,
                SourceFingerprint = request.SourceFingerprint, StoredBytes = stored,
            });
            await db.SaveChangesAsync(ct);

            // Keep the newest captures within both caps: first by count, then by the running total of stored bytes.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM devtools.hook_requests WHERE id IN (
                    SELECT id FROM devtools.hook_requests WHERE hook_id = {hookId} ORDER BY id DESC OFFSET {hook.MaxRequests})
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM devtools.hook_requests WHERE id IN (
                    SELECT id FROM (SELECT id, sum(stored_bytes) OVER (ORDER BY id DESC) AS running
                                    FROM devtools.hook_requests WHERE hook_id = {hookId}) t
                    WHERE t.running > {hook.MaxStoredBytes})
                """, ct);
            await RefreshCountersAsync(hookId, (minute, sameWindow ? hook.WindowCount + 1 : 1, now), ct);
            await tx.CommitAsync(ct);
            return new(IngestOutcome.Stored, hook.ResponseStatus);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return new(IngestOutcome.Busy, 503, 1);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Recomputes stored count and bytes from the rows (inside the caller's transaction, under the row lock); with a window,
    /// also records the accepted request.</summary>
    private async Task RefreshCountersAsync(Guid hookId, (DateTime Minute, int Count, DateTime Now)? accepted, CancellationToken ct)
    {
        var count = await db.HookRequests.CountAsync(r => r.HookId == hookId, ct);
        var bytes = await db.HookRequests.Where(r => r.HookId == hookId).SumAsync(r => (long)r.StoredBytes, ct);
        if (accepted is { } a)
        {
            await db.Hooks.Where(h => h.Id == hookId).ExecuteUpdateAsync(s => s
                .SetProperty(h => h.StoredCount, count).SetProperty(h => h.StoredBytes, bytes)
                .SetProperty(h => h.WindowStart, a.Minute).SetProperty(h => h.WindowCount, a.Count)
                .SetProperty(h => h.ReceivedTotal, h => h.ReceivedTotal + 1).SetProperty(h => h.LastReceivedAt, a.Now), ct);
        }
        else
        {
            await db.Hooks.Where(h => h.Id == hookId).ExecuteUpdateAsync(s => s
                .SetProperty(h => h.StoredCount, count).SetProperty(h => h.StoredBytes, bytes), ct);
        }
    }

    // ---------------------------------------------------------------------------------------------------

    public HookDto ToDto(HookRow h, string fallbackBase) =>
        new(h.Id, h.Label, IngestUrl(h.Token, fallbackBase), DateTime.SpecifyKind(h.ExpiresAt, DateTimeKind.Utc) > Now ? "live" : "read-only",
            h.ResponseStatus, DateTime.SpecifyKind(h.CreatedAt, DateTimeKind.Utc), DateTime.SpecifyKind(h.ExpiresAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(h.PurgeAfter, DateTimeKind.Utc), h.MaxRequests, h.MaxBodyBytes, h.MaxStoredBytes, h.IngestPerMinute,
            h.ReceivedTotal, h.StoredCount, h.StoredBytes, h.LastReceivedAt is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null);

    public static bool IsAllowedStatus(int status) => status is (>= 200 and <= 299) or (>= 400 and <= 599);

    private static string? CleanLabel(string? raw, out Error? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var label = raw.Trim();
        if (label.Length > MaxLabelLength) error = Error.Validation($"The label is at most {MaxLabelLength} characters.");
        else if (label.Any(char.IsControl)) error = Error.Validation("The label cannot contain control characters.");
        return label;
    }

    private static long OwnerLockKey(Guid userId) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.ASCII.GetBytes("zaagl.hooks:" + userId.ToString("D"))), 0);

    private static readonly System.Linq.Expressions.Expression<Func<HookRequestRow, HookRequestSummaryDto>> SummaryProjection =
        r => new HookRequestSummaryDto(r.Id, r.ReceivedAt, r.Method, r.Path, r.Query, r.ContentType, r.BodyBytesTotal, r.BodyTruncated, r.SourceFingerprint);

    private static HookRequestSummaryDto Utc(HookRequestSummaryDto r) => r with { ReceivedAt = DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc) };
}

/// <summary>Purges catchers whose read-only grace ended (captures go with them, cascade).</summary>
public sealed class HookPurgeSweep(DevToolsDbContext db, TimeProvider time) : IDevToolsSweep
{
    public string Name => "webhook catcher purge";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.Hooks.AsNoTracking().Where(h => h.PurgeAfter <= now).OrderBy(h => h.PurgeAfter)
            .Select(h => h.Id).Take(200).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        return await db.Hooks.Where(h => ids.Contains(h.Id) && h.PurgeAfter <= now).ExecuteDeleteAsync(ct);
    }
}
