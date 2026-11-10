using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Contracts;
using Zaagl.Modules.Snippets.Domain;
using Zaagl.Modules.Snippets.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Snippets.Application;

public sealed class SnippetOptions
{
    public const string Section = "Snippets";

    /// <summary>PD-17: at most 100 versions per snippet; the 101st is <c>409 version-limit</c> (interim behaviour).</summary>
    public int MaxVersions { get; set; } = 100;

    /// <summary>A pending idempotency claim older than this belongs to a crashed attempt and may be resumed.</summary>
    public int StaleClaimSeconds { get; set; } = 60;

    /// <summary>ARC-010 §10: combined line count of the two versions a diff may compare.</summary>
    public int MaxDiffLines { get; set; } = 20_000;

    /// <summary>ARC-010 §10: computation budget of one diff.</summary>
    public int DiffTimeoutSeconds { get; set; } = 2;
}

/// <summary>Who creates a snippet. <see cref="CreatedVia"/> is derived by the Api, never sent by clients (ARC-010 §4.1).</summary>
public sealed record SnippetActor(Guid? UserId, byte[]? GuestCreatorKey, CreatedVia Via, GuestAdmission? Admission = null)
{
    public bool IsGuest => UserId is null;
    public ContentOwner Owner => new(UserId, GuestCreatorKey);
}

/// <summary>BE-026: the guest counters (PD-14), supplied by the host because the counters live with the guest
/// identity in Links. <see cref="TryAdmitAsync"/> runs after the plan checks (ARC-010 §4.1 step 6) and must count a
/// slot only when it admits; <see cref="RefundAsync"/> is called when the creation then fails.</summary>
public sealed class GuestAdmission(Func<CancellationToken, Task<bool>> tryAdmit, Func<Task> refund)
{
    public bool Admitted { get; private set; }

    public async Task<bool> TryAdmitAsync(CancellationToken ct) => Admitted = await tryAdmit(ct);

    public async Task RefundAsync()
    {
        if (!Admitted) return;
        Admitted = false;
        await refund();
    }
}

public sealed record SnippetUpdateCommand(string Content, string? Language, SnippetRenderMode RenderMode, int BaseVersion);

/// <summary>An owned snippet with its authoritative state (ARC-006 §2, recomputed from the link snapshot).</summary>
public sealed record OwnedSnippet(ResourceDescriptor Resource, ResourceState State, LinkSnapshot? Link);

/// <summary>
/// BE-008 (ARC-010 §3-§5): create, edit and owner reads. Plan checks come from C5, the envelope and quota from C2
/// (<see cref="IResourceRegistry"/>, <see cref="IContentQuota"/>), the link from C1 (<see cref="ILinkIssuer"/>); this
/// class owns only the <c>snippets</c> schema. Order for create: validate, idempotency claim, plan, register (quota),
/// rows, link, bind, ready.
/// </summary>
public sealed class SnippetService(
    SnippetsDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    ILinkSnapshots snapshots,
    IContentQuota quota,
    IContentPlanProvider plans,
    IContentLinkCommands linkCommands,
    TimeProvider time,
    IOptions<SnippetOptions> options,
    ILogger<SnippetService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------------------------------------------
    // Create (ARC-010 §4.1)
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<SnippetCreatedDto>> CreateAsync(
        SnippetActor actor, CreateSnippetCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = time.GetUtcNow().UtcDateTime;

        // 3. Validate (no I/O).
        var encoded = SnippetText.Encode(command.Content);
        if (!encoded.IsSuccess) return encoded.Error!;
        var bytes = encoded.Value!;
        var language = SnippetText.NormalizeLanguage(command.Language);
        if (!language.IsSuccess) return language.Error!;
        var title = SnippetText.CleanTitle(command.Title);
        if (!title.IsSuccess) return title.Error!;
        if (!Enum.IsDefined(command.RenderMode)) return Error.Validation("Unknown render mode.");
        // Addendum §5.1.3 (to confirm): guests are always keyed until a public guest snippet is approved.
        if (actor.IsGuest && command.Public) return Error.Validation("Guests cannot create public snippets. Remove 'public' to get a private link.");
        if (command.Password is { } pw && (pw.Length < 4 || pw.Length > 128)) return Error.Validation("The password must be 4-128 characters.");
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");

        var sha = SnippetText.Sha256(bytes);
        var scope = actor.UserId is { } userId ? $"u:{userId}" : $"g:{Convert.ToHexString(actor.GuestCreatorKey!)}";
        var requestHash = CreateRequestHash(command, language.Value, title.Value, sha);

        // 4. Idempotency claim.
        var claim = await ClaimAsync(scope, idempotencyKey, requestHash, now, ct);
        if (claim.Replay is { } replay)
        {
            if (replay.Resume is { } resume) return await ResumeCreateAsync(actor, command, title.Value, resume, scope, idempotencyKey, ct);
            return replay.Result!.Value;
        }

        // 5. Plan, 7-11. Any refusal releases the claim so the same key can retry with fixed input.
        var created = await CreateCoreAsync(actor, command, language.Value, title.Value, bytes, sha, scope, idempotencyKey, ct);
        if (!created.IsSuccess)
        {
            if (actor.Admission is { } admission) await admission.RefundAsync();
            await DeleteClaimAsync(scope, idempotencyKey);
        }
        return created;
    }

    private async Task<Result<SnippetCreatedDto>> CreateCoreAsync(
        SnippetActor actor, CreateSnippetCommand command, string? language, string? title, byte[] bytes, byte[] sha,
        string scope, string idempotencyKey, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = actor.IsGuest ? plans.Guest : await plans.GetForUserAsync(actor.UserId!.Value, ct);
        if (bytes.Length > plan.Limits.MaxSnippetBytes)
            return Error.Of(ErrorCodes.SnippetTooLarge, $"A snippet can be at most {plan.Limits.MaxSnippetBytes} bytes on your plan.");
        if (command.Password is not null && !plan.Capabilities.PasswordOnContent)
            return Error.PlanRequired("Password protection is available on the Starter plan.");

        var maxExpiry = now.AddDays(plan.Limits.RetentionDays);
        DateTime expiresAt;
        if (command.ExpiresAt is { } requested)
        {
            expiresAt = requested.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(requested, DateTimeKind.Utc) : requested.ToUniversalTime();
            if (expiresAt <= now) return Error.Validation("The expiry must be in the future.");
            if (expiresAt > maxExpiry)
                return Error.Of(ErrorCodes.ExpiryBeyondPlan, $"Your plan allows at most {plan.Limits.RetentionDays} days.");
        }
        else
        {
            expiresAt = maxExpiry;
        }

        // 6. Guest counters (PD-14): 30 per hour and 60 per 24 h per IP partition, shared with uploads (429 quota).
        if (actor.Admission is { } gate && !await gate.TryAdmitAsync(ct))
            return Error.Of(ErrorCodes.Quota, "Guest limit reached (30 per hour, 60 per day). Sign in or try again later.");

        // 7. Register: Pending, quota reserved (403 quota-exceeded).
        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Snippet, actor.Owner, bytes.Length, expiresAt, title, BurnAfterDownload: false, actor.Via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;

        // 8. Version 1 (covered by the registration's reservation, so it is quota-committed).
        var content = command.Content;
        db.Snippets.Add(new SnippetRow
        {
            ResourceId = resourceId, CurrentVersion = 1, VersionCount = 1, TotalBytes = bytes.Length, CreatedAt = now, UpdatedAt = now,
        });
        db.Versions.Add(new SnippetVersionRow
        {
            ResourceId = resourceId, Version = 1, Content = content, Language = language, RenderMode = command.RenderMode,
            SizeBytes = bytes.Length, LineCount = SnippetText.CountLines(content), Sha256 = sha,
            QuotaCommitted = true, CreatedVia = actor.Via, CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await AbortAsync(resourceId);
            throw;
        }
        await RecordResourceAsync(scope, idempotencyKey, resourceId, ct);

        // 9. Link (idempotency key = resource id; alias errors are 409 as in Phase 0).
        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Snippet, actor.UserId, actor.GuestCreatorKey, expiresAt,
            command.CustomSlug, title, command.Password, RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
            return issued.Error!;
        }

        return await FinishCreateAsync(resourceId, issued.Value!, bytes.Length, SnippetText.CountLines(content), actor.IsGuest, scope, idempotencyKey, ct);
    }

    /// <summary>10-11: bind the link, mark ready, store the replay (without the plain key) and answer.</summary>
    private async Task<Result<SnippetCreatedDto>> FinishCreateAsync(
        Guid resourceId, ContentLinkIssued issued, int size, int lines, bool isGuest, string scope, string idempotencyKey, CancellationToken ct)
    {
        var bound = await resources.BindLinkAsync(resourceId, issued.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        var ready = await resources.MarkReadyAsync(resourceId, size, ct);
        if (!ready.IsSuccess) return ready.Error!;

        var dto = new SnippetCreatedDto(resourceId, issued.Code, issued.ShortUrl, issued.LinkKey, 1, size, lines, issued.ExpiresAt, isGuest);
        await CompleteClaimAsync(scope, idempotencyKey, dto with { LinkKey = null }, ct);
        return dto;
    }

    /// <summary>A crashed earlier attempt with the same key: continue from the link step (the issuer is idempotent per
    /// resource id, so a replayed link comes back without its key, ARC-002 Q1).</summary>
    private async Task<Result<SnippetCreatedDto>> ResumeCreateAsync(
        SnippetActor actor, CreateSnippetCommand command, string? title, Guid resourceId, string scope, string idempotencyKey, CancellationToken ct)
    {
        var version = await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.ResourceId == resourceId && v.Version == 1, ct);
        var resource = await resources.GetAsync(resourceId, ct);
        if (version is null || resource is null)
        {
            await DeleteClaimAsync(scope, idempotencyKey);
            return Error.Conflict("The previous attempt did not finish. Retry the request.");
        }
        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Snippet, actor.UserId, actor.GuestCreatorKey, resource.ExpiresAt,
            command.CustomSlug, title, command.Password, RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess) return issued.Error!;
        return await FinishCreateAsync(resourceId, issued.Value!, version.SizeBytes, version.LineCount, actor.IsGuest, scope, idempotencyKey, ct);
    }

    /// <summary>The compensation after a failure past the register step: the resource never gets a link (so it is
    /// unreachable), its reservation is released and the snippet rows are removed.</summary>
    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.Snippets.Where(s => s.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up snippet resource {ResourceId} after a failed create", resourceId);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Edit (ARC-010 §3, §4.2, §5)
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<SnippetVersionDto>> UpdateAsync(
        Guid resourceId, Guid userId, SnippetUpdateCommand command, string? idempotencyKey, CreatedVia via, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = time.GetUtcNow().UtcDateTime;

        var encoded = SnippetText.Encode(command.Content);
        if (!encoded.IsSuccess) return encoded.Error!;
        var bytes = encoded.Value!;
        var language = SnippetText.NormalizeLanguage(command.Language);
        if (!language.IsSuccess) return language.Error!;
        if (!Enum.IsDefined(command.RenderMode)) return Error.Validation("Unknown render mode.");
        if (command.BaseVersion < 1) return Error.Validation("baseVersion must be at least 1.");
        var sha = SnippetText.Sha256(bytes);

        // Owner only (PD-40): any other principal, admins included, gets 404.
        var owned = await ResolveOwnedAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var plan = await plans.GetForUserAsync(userId, ct);
        if (!plan.Capabilities.ManageContent) return Error.Forbidden("Your plan cannot edit content.");
        if (StateError(owned.Value!) is { } stateError) return stateError;
        if (bytes.Length > plan.Limits.MaxSnippetBytes)
            return Error.Of(ErrorCodes.SnippetTooLarge, $"A snippet can be at most {plan.Limits.MaxSnippetBytes} bytes on your plan.");

        // Optional idempotency on PUT: a retry after a lost response replays the stored 200.
        var scope = $"u:{userId}";
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "put", resourceId.ToString("N"),
                command.BaseVersion.ToString(CultureInfo.InvariantCulture), Convert.ToHexString(sha), language.Value ?? "",
                ((short)command.RenderMode).ToString(CultureInfo.InvariantCulture))));
            var claim = await ClaimAsync(scope, idempotencyKey, hash, now, ct, updateReplay: true);
            if (claim.Replay is { } replay) return replay.UpdateResult!.Value;
        }

        var result = await UpdateCoreAsync(resourceId, command, language.Value, bytes, sha, via, ct);
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (result.IsSuccess) await CompleteClaimAsync(scope, idempotencyKey, result.Value!, ct);
            else await DeleteClaimAsync(scope, idempotencyKey);
        }
        return result;
    }

    private async Task<Result<SnippetVersionDto>> UpdateCoreAsync(
        Guid resourceId, SnippetUpdateCommand command, string? language, byte[] bytes, byte[] sha, CreatedVia via, CancellationToken ct)
    {
        var max = options.Value.MaxVersions;
        var current = await db.Snippets.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
        if (current is null) return Error.NotFound();
        var currentVersion = await db.Versions.AsNoTracking()
            .FirstAsync(v => v.ResourceId == resourceId && v.Version == current.CurrentVersion, ct);

        if (command.BaseVersion != current.CurrentVersion) return Error.Of(ErrorCodes.VersionConflict, $"The snippet is at version {current.CurrentVersion}, not {command.BaseVersion}.");
        // No-op: same text, language and render mode as the current version. Nothing is written or reserved.
        if (currentVersion.Sha256.AsSpan().SequenceEqual(sha) && currentVersion.Language == language && currentVersion.RenderMode == command.RenderMode)
            return ToDto(currentVersion);
        if (current.VersionCount >= max) return Error.Of(ErrorCodes.VersionLimit, $"A snippet can have at most {max} versions.");

        var reserved = await quota.ReserveForResourceAsync(resourceId, bytes.Length, ct);
        if (!reserved.IsSuccess) return reserved.Error!;
        var reservationId = reserved.Value!.ReservationId;

        var now = time.GetUtcNow().UtcDateTime;
        var newVersion = command.BaseVersion + 1;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            var rows = await db.Snippets
                .Where(s => s.ResourceId == resourceId && s.CurrentVersion == command.BaseVersion && s.VersionCount < max)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CurrentVersion, x => x.CurrentVersion + 1)
                    .SetProperty(x => x.VersionCount, x => x.VersionCount + 1)
                    .SetProperty(x => x.TotalBytes, x => x.TotalBytes + bytes.Length)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            if (rows == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                await quota.ReleaseAsync(reservationId, CancellationToken.None);
                var fresh = await db.Snippets.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
                return fresh is { } f && f.CurrentVersion != command.BaseVersion
                    ? Error.Of(ErrorCodes.VersionConflict, $"The snippet is at version {f.CurrentVersion}, not {command.BaseVersion}.")
                    : Error.Of(ErrorCodes.VersionLimit, $"A snippet can have at most {max} versions.");
            }
            db.Versions.Add(new SnippetVersionRow
            {
                ResourceId = resourceId, Version = newVersion, Content = command.Content, Language = language, RenderMode = command.RenderMode,
                SizeBytes = bytes.Length, LineCount = SnippetText.CountLines(command.Content), Sha256 = sha,
                ReservationId = reservationId == Guid.Empty ? null : reservationId, QuotaCommitted = reservationId == Guid.Empty,
                CreatedVia = via, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await quota.ReleaseAsync(reservationId, CancellationToken.None);
            return Error.Of(ErrorCodes.Busy, "The snippet is busy. Try again in a second.");
        }
        catch
        {
            await quota.ReleaseAsync(reservationId, CancellationToken.None);
            throw;
        }

        // F-4: settle the quota. A failure here leaves quota_committed = false and the reservation counted as reserved
        // (fail-safe: the quota is never exceeded), for the repair job of ARC-010 §5.
        if (reservationId != Guid.Empty)
        {
            var committed = await quota.CommitToResourceAsync(reservationId, bytes.Length, ct);
            if (committed.IsSuccess)
                await db.Versions.Where(v => v.ResourceId == resourceId && v.Version == newVersion)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.QuotaCommitted, true), CancellationToken.None);
            else
                logger.LogWarning("Quota commit for snippet {ResourceId} v{Version} failed: {Error}", resourceId, newVersion, committed.Error);
        }

        var saved = await db.Versions.AsNoTracking().FirstAsync(v => v.ResourceId == resourceId && v.Version == newVersion, ct);
        return ToDto(saved);
    }


    // ---------------------------------------------------------------------------------------------------
    // Owner reads
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The owner's snippet with the authoritative state. Not found for another owner, a guest snippet, another
    /// kind or a purgeable resource (PD-40: no difference is visible).</summary>
    public async Task<Result<OwnedSnippet>> ResolveOwnedAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind != ResourceKind.Snippet || resource.Owner.UserId != userId) return Error.NotFound();

        LinkSnapshot? link = resource.LinkId is { } linkId ? await snapshots.GetSnapshotByIdAsync(linkId, ct) : null;
        LinkState? linkState = null;
        if (link is not null)
        {
            linkState = LinkStateCalculator.Compute(link, time.GetUtcNow().UtcDateTime);
            if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        }
        var state = ResourceStateCalculator.Compute(linkState, resource.Processing, resource.BurnClaimedAt, null);
        return state == ResourceState.Purgeable ? Error.NotFound() : Result<OwnedSnippet>.Ok(new OwnedSnippet(resource, state, link));
    }

    /// <summary>Why a snippet that is not ready cannot be read or edited: <c>409 processing</c>, <c>410</c> with the
    /// specific code, or <c>422 content-rejected</c>. Null when ready.</summary>
    public static Error? StateError(OwnedSnippet owned) => owned.State switch
    {
        ResourceState.Ready => null,
        ResourceState.Processing => Error.Of(ErrorCodes.Processing, "This snippet is still being prepared."),
        ResourceState.Rejected => Error.Of(ErrorCodes.ContentRejected, "This content is not available."),
        ResourceState.Deleted => Error.Of(ErrorCodes.Deleted, "This snippet was deleted."),
        ResourceState.Disabled => owned.Link?.DisabledKind is "abuse" or "safety"
            ? Error.Of(ErrorCodes.TakenDown, "This snippet was removed under the acceptable use policy.")
            : Error.Of(ErrorCodes.Disabled, "This snippet has been disabled."),
        ResourceState.Burned => Error.Of(ErrorCodes.Burned, "This content is gone."),
        _ => Error.Of(ErrorCodes.Expired, "This snippet has expired."),
    };

    /// <summary>Metadata of every version, newest first (at most 100, no paging).</summary>
    public async Task<IReadOnlyList<SnippetVersionDto>> ListVersionsAsync(Guid resourceId, CancellationToken ct) =>
        (await db.Versions.AsNoTracking().Where(v => v.ResourceId == resourceId)
            .OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.SizeBytes, v.LineCount, v.CreatedAt, v.Language, v.RenderMode })
            .ToListAsync(ct))
        .Select(v => new SnippetVersionDto(v.Version, v.SizeBytes, v.LineCount, DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc), v.Language, v.RenderMode))
        .ToList();

    public async Task<SnippetVersionDto?> GetCurrentVersionAsync(Guid resourceId, CancellationToken ct)
    {
        var snippet = await db.Snippets.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
        if (snippet is null) return null;
        var version = await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.ResourceId == resourceId && v.Version == snippet.CurrentVersion, ct);
        return version is null ? null : ToDto(version);
    }

    /// <summary>BE-009 (ARC-010 §10): line diff of two versions. Cost is bounded three ways: the combined line count
    /// (from stored metadata, before any text is loaded) must be within <see cref="SnippetOptions.MaxDiffLines"/>,
    /// the text is at most twice the plan cap by construction, and the computation runs under a time budget. The
    /// caller has already resolved ownership and the ready state.</summary>
    public async Task<Result<SnippetDiffDto>> DiffAsync(Guid resourceId, int from, int to, bool ignoreWhitespace, CancellationToken ct)
    {
        if (from < 1 || to < 1) return Error.Validation("Versions start at 1.");
        if (from == to) return Error.Validation("from and to must be different versions.");

        var meta = await db.Versions.AsNoTracking()
            .Where(v => v.ResourceId == resourceId && (v.Version == from || v.Version == to))
            .Select(v => new { v.Version, v.LineCount }).ToListAsync(ct);
        if (meta.Count != 2) return Error.NotFound("Version not found.");
        if ((long)meta[0].LineCount + meta[1].LineCount > options.Value.MaxDiffLines)
            return Error.Of(ErrorCodes.DiffTooLarge, $"These versions are too large to compare (more than {options.Value.MaxDiffLines} lines together).");

        var texts = await db.Versions.AsNoTracking()
            .Where(v => v.ResourceId == resourceId && (v.Version == from || v.Version == to))
            .Select(v => new { v.Version, v.Content }).ToListAsync(ct);
        var left = texts.First(t => t.Version == from).Content;
        var right = texts.First(t => t.Version == to).Content;

        try
        {
            var budget = TimeSpan.FromSeconds(options.Value.DiffTimeoutSeconds);
            // DiffPlex cannot be cancelled; WaitAsync releases the request at the deadline and the abandoned
            // computation is bounded by the line cap above.
            var task = Task.Run(() => SnippetDiff.Compute(from, to, left, right, ignoreWhitespace), CancellationToken.None);
            return await task.WaitAsync(budget, time, ct);
        }
        catch (TimeoutException)
        {
            return Error.Of(ErrorCodes.DiffTooLarge, "The comparison took too long. These versions are too different to compare.");
        }
    }

    /// <summary>Owner delete (PD-55): the link becomes deleted (410), text and quota stay until purge. Deleting an
    /// already deleted snippet succeeds. Content removed for abuse or safety cannot be deleted by its owner.</summary>
    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ResolveOwnedAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var o = owned.Value!;
        if (o.State == ResourceState.Deleted) return true;
        if (o.State == ResourceState.Disabled && o.Link?.DisabledKind is "abuse" or "safety") return StateError(o)!;
        if (o.Resource.LinkId is not { } linkId) return Error.NotFound();
        var deleted = await linkCommands.DeleteAsync(linkId, ct);
        // not_found here means a concurrent delete already finished: the outcome the caller asked for.
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }

    public async Task<int> VersionCountAsync(Guid resourceId, CancellationToken ct) =>
        await db.Snippets.AsNoTracking().Where(s => s.ResourceId == resourceId).Select(s => s.VersionCount).FirstOrDefaultAsync(ct);

    private static SnippetVersionDto ToDto(SnippetVersionRow v) =>
        new(v.Version, v.SizeBytes, v.LineCount, DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc), v.Language, v.RenderMode);

    // ---------------------------------------------------------------------------------------------------
    // Idempotency claims (scope = u:<owner> | g:<creator key>, ARC-010 §4.1 step 4)
    // ---------------------------------------------------------------------------------------------------

    private sealed record ClaimOutcome(ClaimReplay? Replay);

    /// <param name="Result">Create: the stored 201 body or a refusal.</param>
    /// <param name="UpdateResult">PUT: the stored 200 body or a refusal.</param>
    /// <param name="Resume">A stale pending claim of a crashed create: its resource id.</param>
    private sealed record ClaimReplay(Result<SnippetCreatedDto>? Result, Result<SnippetVersionDto>? UpdateResult, Guid? Resume);

    private async Task<ClaimOutcome> ClaimAsync(string scope, string key, byte[] hash, DateTime now, CancellationToken ct, bool updateReplay = false)
    {
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO snippets.idempotency_keys (scope, key, request_hash, status, created_at)
            VALUES ({scope}, {key}, {hash}, 0, {now}) ON CONFLICT DO NOTHING
            """, ct);
        if (inserted > 0) return new ClaimOutcome(null);

        var existing = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Scope == scope && k.Key == key, ct);
        Error Busy() => Error.Conflict("A request with this Idempotency-Key is still in progress. Retry shortly.");
        ClaimReplay Fail(Error e) => updateReplay
            ? new ClaimReplay(null, Result<SnippetVersionDto>.Fail(e), null)
            : new ClaimReplay(Result<SnippetCreatedDto>.Fail(e), null, null);

        if (existing is null) return new ClaimOutcome(Fail(Busy()));
        if (!existing.RequestHash.AsSpan().SequenceEqual(hash))
            return new ClaimOutcome(Fail(Error.Of(ErrorCodes.IdempotencyMismatch, "This Idempotency-Key was already used with a different request.")));
        if (existing.Status == 1)
        {
            return new ClaimOutcome(updateReplay
                ? new ClaimReplay(null, Result<SnippetVersionDto>.Ok(JsonSerializer.Deserialize<SnippetVersionDto>(existing.ResponseBody!, Json)!), null)
                : new ClaimReplay(Result<SnippetCreatedDto>.Ok(JsonSerializer.Deserialize<SnippetCreatedDto>(existing.ResponseBody!, Json)!), null, null));
        }
        // Pending: still running, or a crashed create that can be resumed (a PUT claim is simply released).
        var stale = existing.CreatedAt <= now.AddSeconds(-options.Value.StaleClaimSeconds);
        if (stale && !updateReplay && existing.ResourceId is { } resourceId) return new ClaimOutcome(new ClaimReplay(null, null, resourceId));
        if (stale)
        {
            await DeleteClaimAsync(scope, key);
            return await ClaimAsync(scope, key, hash, now, ct, updateReplay);
        }
        return new ClaimOutcome(Fail(Busy()));
    }

    private Task RecordResourceAsync(string scope, string key, Guid resourceId, CancellationToken ct) =>
        db.IdempotencyKeys.Where(k => k.Scope == scope && k.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ResourceId, resourceId), ct);

    private Task CompleteClaimAsync<T>(string scope, string key, T body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, Json);
        return db.IdempotencyKeys.Where(k => k.Scope == scope && k.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, (short)1).SetProperty(k => k.ResponseBody, json), ct);
    }

    private Task DeleteClaimAsync(string scope, string key) =>
        db.IdempotencyKeys.Where(k => k.Scope == scope && k.Key == key).ExecuteDeleteAsync(CancellationToken.None);

    /// <summary>ARC-010 §4.1 step 4: SHA-256 over the canonical fields plus the hash of the text.</summary>
    private static byte[] CreateRequestHash(CreateSnippetCommand c, string? language, string? title, byte[] contentSha) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "create",
            Convert.ToHexString(contentSha),
            language ?? "",
            ((short)c.RenderMode).ToString(CultureInfo.InvariantCulture),
            title ?? "",
            c.ExpiresAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) ?? "",
            c.Password is { Length: > 0 } ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Password))) : "",
            c.CustomSlug?.Trim() ?? "",
            c.Public ? "1" : "0")));
}
