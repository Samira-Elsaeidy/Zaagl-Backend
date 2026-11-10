using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Domain;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary>Who uploads. Slice 1 accepts accounts only (JWT, device session, API key with <c>files:write</c>); the guest key is slice 2.</summary>
public sealed record UploadActor(
    Guid? UserId, byte[]? GuestCreatorKey, CreatedVia Via,
    Func<CancellationToken, Task<bool>>? AdmitGuest = null, Func<Task>? RefundGuest = null)
{
    public ContentOwner Owner => new(UserId, GuestCreatorKey);
}

public sealed record UploadCreated(Guid Id, DateTime ExpiresAt);

public sealed record UploadOffset(long Offset, long Length, DateTime ExpiresAt);

/// <summary>
/// The tus session and the hand-off to Resources, Links and the worker (ARC-009 sections 4 to 6). tus is hand-written (the wire
/// contract is the contract, not a library): create, offset, append, abort and <c>/complete</c>. Every state change of a session
/// is a conditional UPDATE, and a per-session advisory lock serialises PATCH and <c>/complete</c> across API instances.
/// </summary>
public sealed class UploadService(
    FilesDbContext db,
    IContentQuota quota,
    IContentPlanProvider plans,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    TempUploadFiles temp,
    DiskAdmission admission,
    TimeProvider time,
    IOptions<UploadOptions> uploads,
    IOptions<ScanOptions> scan,
    [FromKeyedServices(FilesDbContext.Schema)] TaskQueue tasks,
    ILogger<UploadService> logger)
{
    private const int MaxMetadataBytes = 4096;
    private const int MaxFileNameBytes = 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string ConnectionString => db.Database.GetConnectionString()
        ?? throw new InvalidOperationException("The files connection string is not available.");

    // ---------------------------------------------------------------------------------------------------
    // OPTIONS: the size a caller may announce
    // ---------------------------------------------------------------------------------------------------

    public async Task<long> MaxSizeForAsync(Guid? userId, CancellationToken ct) =>
        userId is { } id ? (await plans.GetForUserAsync(id, ct)).Limits.MaxFileBytes : plans.Guest.Limits.MaxFileBytes;

    // ---------------------------------------------------------------------------------------------------
    // Create (ARC-009 section 5: order of refusals; everything before the first byte)
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<UploadCreated>> CreateAsync(
        UploadActor actor, long? declaredLength, bool deferLength, string? metadataHeader, string? idempotencyKey, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var options = uploads.Value;

        // 1. Credential. No credential and guests off: 503 guest_creation_paused (the host decides 401 for a bad credential).
        var userId = actor.UserId;
        if (userId is null && (actor.GuestCreatorKey is null || !options.GuestsEnabled))
            return Error.Of(ErrorCodes.GuestCreationPaused, "Uploads need an account for now. Sign in to upload.");

        // 3. Validate, no I/O.
        if (deferLength || declaredLength is null)
            return Error.Of(ErrorCodes.UploadLengthRequired, "Upload-Length is required; a deferred length is not supported.");
        if (declaredLength < 1) return Error.Validation("An empty file cannot be uploaded.");
        var length = declaredLength.Value;

        if (metadataHeader is not null && Encoding.UTF8.GetByteCount(metadataHeader) > MaxMetadataBytes)
            return Error.Validation("Upload-Metadata is at most 4,096 bytes.");
        var metadata = FileNames.ParseTusMetadata(metadataHeader);
        if (metadata is null) return Error.Validation("Upload-Metadata is not valid (key, space, base64 value; comma separated).");
        if (!metadata.TryGetValue(UploadProtocol.MetadataKind, out var kind) || kind is not ("file" or "media"))
            return Error.Validation("Upload-Metadata needs kind=media or kind=file.");
        if (!options.KindEnabled(kind))
            return Error.Validation($"Uploading kind '{kind}' is not available yet.");
        metadata.TryGetValue(UploadProtocol.MetadataFileName, out var rawName);
        metadata.TryGetValue(UploadProtocol.MetadataFileType, out var rawType);
        var declaredName = rawName is null ? null : CutUtf8(rawName, MaxFileNameBytes);
        var declaredType = rawType is null ? null : rawType[..Math.Min(rawType.Length, 255)];

        // 4. Idempotency (optional on create): the same key with the same length returns the same session.
        byte[]? keyHash = null;
        if (!string.IsNullOrEmpty(idempotencyKey) && userId is not null)
        {
            keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
            var existing = await db.UploadSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.OwnerId == userId && s.CreateKeyHash == keyHash, ct);
            if (existing is not null)
                return existing.DeclaredLength == length && existing.IntendedKind == KindValue(kind)
                    ? new UploadCreated(existing.Id, existing.ExpiresAt)
                    : Error.Of(ErrorCodes.IdempotencyMismatch, "This Idempotency-Key was used for a different upload.");
        }

        // 5. Plan.
        var plan = userId is { } uid ? await plans.GetForUserAsync(uid, ct) : plans.Guest;
        if (length > plan.Limits.MaxFileBytes)
            return Error.Of(ErrorCodes.FileTooLarge, $"A file can be at most {plan.Limits.MaxFileBytes} bytes on your plan.");

        // Burn (ARC-019 section 1) is refused before any byte: files only, and only with the capability. /complete checks again.
        if (metadata.TryGetValue(UploadProtocol.MetadataBurn, out var burnValue) && burnValue is "1" or "true")
        {
            if (kind != "file") return Error.Validation("burnAfterDownload applies to files only.");
            if (!plan.Capabilities.BurnAfterDownload) return Error.PlanRequired("Burn after downloading is available on the Starter plan.");
        }

        // 5b. Guest counters (PD-14, 429 quota), after the plan checks; refunded when the create fails.
        var admitted = false;
        if (userId is null && actor.AdmitGuest is { } admit)
        {
            if (!await admit(ct)) return Error.Of(ErrorCodes.Quota, "Guest limit reached (30 per hour, 60 per day). Sign in or try again later.");
            admitted = true;
        }

        // 6. Disk admission.
        if (await admission.CheckAsync(length, ct) is { } full) { await RefundAsync(admitted, actor); return full; }

        // 7. Reserve the declared bytes (403 quota-exceeded) and record the session.
        var reserved = await quota.ReserveAsync(actor.Owner, length, ct);
        if (!reserved.IsSuccess) { await RefundAsync(admitted, actor); return reserved.Error!; }
        var reservationId = reserved.Value!.ReservationId;

        var session = new UploadSessionRow
        {
            Id = Guid.CreateVersion7(),
            OwnerId = userId,
            GuestCreatorKey = userId is null ? actor.GuestCreatorKey : null,
            DeclaredLength = length,
            ReceivedLength = 0,
            DeclaredName = declaredName,
            DeclaredType = declaredType,
            IntendedKind = KindValue(kind),
            MaxBytes = plan.Limits.MaxFileBytes,
            ReservationId = reservationId == Guid.Empty ? null : reservationId,
            Status = UploadSessionStatus.Open,
            CreatedVia = (short)actor.Via,
            CreateKeyHash = keyHash,
            CreatedAt = now,
            LastActivityAt = now,
            ExpiresAt = now.AddHours(Math.Max(1, options.SessionIdleHours)),
        };
        try
        {
            db.UploadSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && keyHash is not null)
        {
            // A parallel create with the same key won: give the extra reservation back and answer with that session.
            await quota.ReleaseAsync(reservationId, CancellationToken.None);
            db.ChangeTracker.Clear();
            var winner = await db.UploadSessions.AsNoTracking().FirstAsync(s => s.OwnerId == userId && s.CreateKeyHash == keyHash, ct);
            return new UploadCreated(winner.Id, winner.ExpiresAt);
        }
        catch
        {
            await quota.ReleaseAsync(reservationId, CancellationToken.None);
            await RefundAsync(admitted, actor);
            throw;
        }

        // 8. After commit, the empty temp file. A crash before this leaves a session with no file: the first PATCH creates it.
        try
        {
            temp.CreateEmpty(session.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not create the temp file of upload {UploadId}; aborting the session", session.Id);
            await AbortCoreAsync(session.Id, CancellationToken.None);
            return Error.Of(ErrorCodes.InsufficientStorage, "The server cannot take this upload right now. Try again later.");
        }
        return new UploadCreated(session.Id, session.ExpiresAt);
    }

    // ---------------------------------------------------------------------------------------------------
    // HEAD
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<UploadOffset>> GetOffsetAsync(UploadActor actor, Guid id, CancellationToken ct)
    {
        var session = await LoadAsync(actor, id, ct);
        if (session is null) return Error.NotFound();
        if (IsGone(session)) return Error.Of(ErrorCodes.UploadExpired, "This upload expired. Start a new upload.");
        // The row's offset is the truth; a file longer than the row is cut back by the next PATCH.
        return new UploadOffset(session.ReceivedLength, session.DeclaredLength, session.ExpiresAt);
    }

    // ---------------------------------------------------------------------------------------------------
    // PATCH
    // ---------------------------------------------------------------------------------------------------

    /// <returns>The new offset and expiry.</returns>
    public async Task<Result<UploadOffset>> AppendAsync(
        UploadActor actor, Guid id, long offset, long? contentLength, Stream body, CancellationToken ct)
    {
        var session = await LoadAsync(actor, id, ct);
        if (session is null) return Error.NotFound();
        var early = StatusError(session);
        if (early is not null) return early;

        var maxChunk = uploads.Value.MaxChunkBytes;
        if (contentLength is > 0 && contentLength > maxChunk)
            return Error.Of(ErrorCodes.ChunkTooLarge, $"A chunk is at most {maxChunk} bytes.");

        await using var gate = await AdvisoryLock.TryAcquireAsync(ConnectionString, AdvisoryLock.KeyFor(id), TimeSpan.Zero, time, ct);
        if (gate is null) return Error.Of(ErrorCodes.UploadOffsetMismatch, "Another request is writing this upload.");

        // Re-read under the lock: the loser of a race sees the winner's offset.
        db.ChangeTracker.Clear();
        session = await LoadAsync(actor, id, ct);
        if (session is null) return Error.NotFound();
        if (StatusError(session) is { } changed) return changed;
        if (offset != session.ReceivedLength)
            return Error.Of(ErrorCodes.UploadOffsetMismatch, $"The upload is at offset {session.ReceivedLength}, not {offset}.");

        var remaining = session.DeclaredLength - session.ReceivedLength;
        if (contentLength > remaining)
            return Error.Of(ErrorCodes.FileTooLarge, "The chunk goes past the declared upload length.");

        long written = 0;
        try
        {
            await temp.AppendAsync(id, offset, body, Math.Min(remaining, maxChunk), w => written = w, ct);
        }
        catch (UploadBodyTooLargeException)
        {
            return remaining <= maxChunk
                ? Error.Of(ErrorCodes.FileTooLarge, "The chunk goes past the declared upload length.")
                : Error.Of(ErrorCodes.ChunkTooLarge, $"A chunk is at most {maxChunk} bytes.");
        }
        catch (TempFileShortException ex)
        {
            // The file was lost or shortened: the row follows the file, the client learns the real offset from HEAD.
            var actual = Math.Clamp(ex.ActualLength, 0, session.DeclaredLength);
            await db.UploadSessions.Where(s => s.Id == id && s.Status == UploadSessionStatus.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceivedLength, actual), CancellationToken.None);
            return Error.Of(ErrorCodes.UploadOffsetMismatch, $"The upload is at offset {actual}, not {offset}.");
        }
        catch
        {
            // An interrupted request (client gone, disk error): keep the bytes that were flushed, so a resume continues there.
            if (written > 0) await RecordProgressAsync(session, offset + written, CancellationToken.None);
            throw;
        }

        var newOffset = offset + written;
        await RecordProgressAsync(session, newOffset, ct);
        var current = await db.UploadSessions.AsNoTracking().FirstAsync(s => s.Id == id, ct);
        return new UploadOffset(current.ReceivedLength, current.DeclaredLength, current.ExpiresAt);
    }

    /// <summary>Advances the row to <paramref name="newOffset"/>; the PATCH that reaches the declared length makes the session
    /// Complete and the file read-only (ARC-009 4.3). Guarded by status and offset.</summary>
    private async Task RecordProgressAsync(UploadSessionRow session, long newOffset, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddHours(Math.Max(1, uploads.Value.SessionIdleHours));
        var complete = newOffset == session.DeclaredLength;
        var before = session.ReceivedLength;
        var rows = await db.UploadSessions
            .Where(s => s.Id == session.Id && s.Status == UploadSessionStatus.Open && s.ReceivedLength == before)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ReceivedLength, newOffset)
                .SetProperty(x => x.Status, complete ? UploadSessionStatus.Complete : UploadSessionStatus.Open)
                .SetProperty(x => x.LastActivityAt, now)
                .SetProperty(x => x.ExpiresAt, expires), ct);
        if (rows > 0 && complete) temp.MakeReadOnly(session.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // DELETE
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<bool>> AbortAsync(UploadActor actor, Guid id, CancellationToken ct)
    {
        var session = await LoadAsync(actor, id, ct);
        // The first call aborts; later calls (and finalized sessions) are 404.
        if (session is null || session.Status is not (UploadSessionStatus.Open or UploadSessionStatus.Complete)) return Error.NotFound();

        await using var gate = await AdvisoryLock.TryAcquireAsync(ConnectionString, AdvisoryLock.KeyFor(id), TimeSpan.FromSeconds(3), time, ct);
        if (gate is null) return Error.Of(ErrorCodes.Busy, "The upload is busy. Try again in a second.");
        return await AbortCoreAsync(id, ct) ? true : Error.NotFound();
    }

    private async Task<bool> AbortCoreAsync(Guid id, CancellationToken ct)
    {
        var reservation = await db.UploadSessions.AsNoTracking().Where(s => s.Id == id).Select(s => s.ReservationId).FirstOrDefaultAsync(ct);
        var rows = await db.UploadSessions
            .Where(s => s.Id == id && (s.Status == UploadSessionStatus.Open || s.Status == UploadSessionStatus.Complete))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, UploadSessionStatus.Aborted), ct);
        if (rows == 0) return false;
        await temp.DeleteAsync(id, CancellationToken.None);
        if (reservation is { } reservationId) await quota.ReleaseAsync(reservationId, CancellationToken.None);
        return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // Complete (ARC-009 section 6)
    // ---------------------------------------------------------------------------------------------------

    public async Task<Result<CompleteUploadResponse>> CompleteAsync(
        UploadActor actor, Guid id, CompleteUploadRequest request, string idempotencyKey, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        // 1. Principal and session (404 for another principal).
        var preview = await LoadAsync(actor, id, ct);
        if (preview is null) return Error.NotFound();
        var userId = actor.UserId;
        if (userId is null && actor.GuestCreatorKey is null) return Error.NotFound();

        await using var gate = await AdvisoryLock.TryAcquireAsync(ConnectionString, AdvisoryLock.KeyFor(id), TimeSpan.FromSeconds(3), time, ct);
        if (gate is null) return Error.Of(ErrorCodes.Busy, "This upload is being finished by another request. Try again in a second.");

        db.ChangeTracker.Clear();
        var session = await LoadAsync(actor, id, ct);
        if (session is null) return Error.NotFound();

        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var bodyHash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            request.Title ?? "", request.ExpiresAt?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "",
            request.BurnAfterDownload ? "1" : "0", request.Password ?? "", request.CustomSlug ?? "", request.Public ? "1" : "0")));

        switch (session.Status)
        {
            case UploadSessionStatus.Open:
                return Error.Of(ErrorCodes.UploadIncomplete, "The upload has not received all its bytes yet.");
            case UploadSessionStatus.Aborted or UploadSessionStatus.Expired:
                return Error.Of(ErrorCodes.UploadExpired, "This upload expired. Start a new upload.");
            case UploadSessionStatus.Finalized:
                if (session.CompleteKeyHash is null || !session.CompleteKeyHash.AsSpan().SequenceEqual(keyHash))
                    return Error.Of(ErrorCodes.AlreadyFinalized, "This upload was already finished.");
                if (session.CompleteBodyHash is null || !session.CompleteBodyHash.AsSpan().SequenceEqual(bodyHash))
                    return Error.Of(ErrorCodes.IdempotencyMismatch, "This Idempotency-Key was used with a different request.");
                if (session.CompleteResponse is { } stored && JsonSerializer.Deserialize<CompleteUploadResponse>(stored, Json) is { } replay)
                    return replay with { LinkKey = null }; // ARC-002 Q1 (OPEN): the key is returned once.
                return Error.Of(ErrorCodes.AlreadyFinalized, "This upload was already finished.");
        }

        // 2. Validate the body (no I/O). Burn applies to files only (ARC-019 section 1): media is a 400.
        if (request.BurnAfterDownload && session.IntendedKind != (short)ResourceKind.File) return Error.Validation("burnAfterDownload applies to files only.");
        if (userId is null && request.Public) return Error.Validation("Guest content cannot be public (addendum 5.1.3 to confirm).");
        var title = CleanTitle(request.Title);
        if (request.Title is { } rawTitle && rawTitle.Trim().Length > 200) return Error.Validation("The title is at most 200 characters.");
        if (request.Password is { } pw && (pw.Length < 4 || pw.Length > 128)) return Error.Validation("The password must be 4-128 characters.");
        var customSlug = string.IsNullOrWhiteSpace(request.CustomSlug) ? null : request.CustomSlug.Trim();
        if (customSlug is { Length: > 0 } slug && slug.Length is < 4 or > 64) return Error.Validation("The alias must be 4-64 characters.");

        var plan = userId is { } planUser ? await plans.GetForUserAsync(planUser, ct) : plans.Guest;
        if (request.BurnAfterDownload && !plan.Capabilities.BurnAfterDownload)
            return Error.PlanRequired("Burn after downloading is available on the Starter plan.");
        if (request.Password is not null && !plan.Capabilities.PasswordOnContent)
            return Error.PlanRequired("Password protection is available on the Starter plan.");
        var maxExpiry = now.AddDays(plan.Limits.RetentionDays);
        DateTime expiresAt;
        if (request.ExpiresAt is { } requested)
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

        await db.UploadSessions.Where(s => s.Id == id && s.Status == UploadSessionStatus.Complete)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompleteKeyHash, keyHash).SetProperty(x => x.CompleteBodyHash, bodyHash), ct);

        // 3 to 4. Sniff, fix the kind, register (adopting the reservation). A retry continues at step 5 when resource_id is set.
        var detected = (string?)null;
        var detectedExt = (string?)null;
        if (session.ResourceId is null)
        {
            temp.MakeReadOnly(id); // idempotent: closes the window of a crash between the last PATCH and its chmod
            byte[] head;
            try
            {
                head = await temp.ReadHeadAsync(id, MediaSniffer.HeadBytes, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                logger.LogError("Upload {UploadId} is Complete but its temp file is missing", id);
                return Error.Of(ErrorCodes.UploadExpired, "The upload's bytes are gone. Start a new upload.");
            }
            // Type policy (ARC-009 9.2, PD-16 interim). A declared media upload that is not an allowed image becomes a file (re-kinding).
            var classified = FileTypePolicy.Classify(head);
            var type = classified.Type;
            var kind = (ResourceKind)session.IntendedKind;
            if (kind == ResourceKind.Media && (type is null || !MediaSniffer.IsMediaAllowed(type.Mime)))
            {
                if (!uploads.Value.KindEnabled("file")) return Error.Of(ErrorCodes.ContentRejected, "This file type is not supported yet.");
                kind = ResourceKind.File;
            }
            if (classified.Class == FileClass.EncryptedArchive) return Error.Of(ErrorCodes.ContentRejected, "Encrypted archives are not accepted.");
            var blocked = classified.Class == FileClass.Executable
                || FileTypePolicy.HasBlockedExtension(FileNames.Sanitize(session.DeclaredName, type?.Extension)); // the display name, never a path
            if (blocked && uploads.Value.BlocksExecutablesFor(isGuest: userId is null, isPaid: plan.Capabilities.IsPaid))
                return Error.Of(ErrorCodes.ContentRejected, "Executable files are not accepted on your plan.");
            detected = type?.Mime;
            detectedExt = type?.Extension;

            var registered = await resources.RegisterAsync(new RegisterResourceCommand(
                kind, actor.Owner, session.DeclaredLength, expiresAt, title,
                request.BurnAfterDownload, actor.Via, AdoptReservationId: session.ReservationId), ct);
            if (!registered.IsSuccess) return registered.Error!;

            var resourceId = registered.Value!.ResourceId;
            await db.UploadSessions.Where(s => s.Id == id && s.ResourceId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResourceId, resourceId), ct);
            session.ResourceId = resourceId;
            await EnsureStorageObjectAsync(session, detected, detectedExt, now, ct);
        }

        var resource = await resources.GetAsync(session.ResourceId!.Value, ct);
        if (resource is null) return Error.Conflict("The upload's resource is missing. Start a new upload.");
        var linkExpiry = resource.ExpiresAt;

        // 5. The link (idempotent per resource id; a replay comes back without its key), then bind.
        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resource.ResourceId, resource.Kind == ResourceKind.File ? LinkTargetKind.File : LinkTargetKind.Media, userId, userId is null ? actor.GuestCreatorKey : null, linkExpiry,
            customSlug, title, request.Password, RequireLinkKey: !request.Public), ct);
        if (!issued.IsSuccess) return issued.Error!;
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resource.ResourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;

        // 6. One Files transaction: the scan task and the session Finalized (with the replay body, without the key).
        var response = new CompleteUploadResponse(resource.ResourceId, link.Code, link.ShortUrl, link.LinkKey, "processing", link.ExpiresAt);
        var storedBody = JsonSerializer.Serialize(response with { LinkKey = null }, Json);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await EnsureStorageObjectAsync(session, detected, detectedExt, now, ct);
            await tasks.EnqueueAsync(db, "scan", $"scan:{resource.ResourceId}",
                JsonSerializer.Serialize(new { resourceId = resource.ResourceId }, Json),
                Math.Max(1, scan.Value.MaxAttempts), now, now, ct);
            await db.UploadSessions.Where(s => s.Id == id && s.Status == UploadSessionStatus.Complete)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, UploadSessionStatus.Finalized)
                    .SetProperty(x => x.CompleteResponse, storedBody), ct);
            await tx.CommitAsync(ct);
        }
        return response;
    }

    /// <summary>The <c>storage_objects</c> row (area Temp, scan Pending). Idempotent: the object id is the session id.</summary>
    private async Task EnsureStorageObjectAsync(UploadSessionRow session, string? detectedMime, string? detectedExt, DateTime now, CancellationToken ct)
    {
        if (await db.StorageObjects.AnyAsync(o => o.Id == session.Id, ct)) return;
        db.StorageObjects.Add(new StorageObjectRow
        {
            Id = session.Id,
            ResourceId = session.ResourceId!.Value,
            Area = StorageArea.Temp,
            SizeBytes = session.DeclaredLength,
            DetectedMime = detectedMime,
            DetectedExt = detectedExt,
            DisplayName = FileNames.Sanitize(session.DeclaredName, detectedExt),
            ScanStatus = ScanVerdict.Pending,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    // ---------------------------------------------------------------------------------------------------
    // /uploads/simple (one request): the length is known only after the body was streamed
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// After the single body was appended into the session created from the <c>Content-Length</c> upper bound, the session is cut to what
    /// arrived: declared length = received, status Complete, and the reservation is swapped for the real size (guests have none).
    /// An empty body aborts. Then the caller runs <see cref="CompleteAsync"/> as for tus.
    /// </summary>
    public async Task<Result<bool>> SealPartialAsync(UploadActor actor, Guid id, CancellationToken ct)
    {
        var session = await LoadAsync(actor, id, ct);
        if (session is null) return Error.NotFound();
        if (session.ReceivedLength < 1)
        {
            await AbortCoreAsync(id, CancellationToken.None);
            return Error.Validation("An empty file cannot be uploaded.");
        }
        var received = session.ReceivedLength;
        var old = session.ReservationId;
        if (session.Status == UploadSessionStatus.Open)
        {
            if (old is { } oldReservation) await quota.ReleaseAsync(oldReservation, ct);
            Guid? replacement = null;
            var reserved = await quota.ReserveAsync(actor.Owner, received, ct);
            if (!reserved.IsSuccess)
            {
                await db.UploadSessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservationId, (Guid?)null), CancellationToken.None);
                await AbortCoreAsync(id, CancellationToken.None);
                return reserved.Error!;
            }
            if (reserved.Value!.ReservationId != Guid.Empty) replacement = reserved.Value.ReservationId;
            var rows = await db.UploadSessions.Where(s => s.Id == id && s.Status == UploadSessionStatus.Open)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DeclaredLength, received)
                    .SetProperty(x => x.ReservationId, replacement)
                    .SetProperty(x => x.Status, UploadSessionStatus.Complete), ct);
            if (rows == 0 && replacement is { } orphan) await quota.ReleaseAsync(orphan, CancellationToken.None);
            temp.MakeReadOnly(id);
        }
        return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The session of this principal; another principal's session does not exist (404, ARC-009 1).</summary>
    private static async Task RefundAsync(bool admitted, UploadActor actor)
    {
        if (admitted && actor.RefundGuest is { } refund) await refund();
    }

    private async Task<UploadSessionRow?> LoadAsync(UploadActor actor, Guid id, CancellationToken ct)
    {
        if (actor.UserId is { } userId)
            return await db.UploadSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == userId, ct);
        if (actor.GuestCreatorKey is { } key)
            return await db.UploadSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.GuestCreatorKey == key, ct);
        return null;
    }

    private bool IsGone(UploadSessionRow session) =>
        session.Status is UploadSessionStatus.Aborted or UploadSessionStatus.Expired
        || (session.Status is UploadSessionStatus.Open or UploadSessionStatus.Complete && session.ExpiresAt <= time.GetUtcNow().UtcDateTime);

    /// <summary>PATCH on a session that is not Open: 410 for Expired or Aborted, 409 for Complete or Finalized.</summary>
    private Error? StatusError(UploadSessionRow session)
    {
        if (IsGone(session)) return Error.Of(ErrorCodes.UploadExpired, "This upload expired. Start a new upload.");
        return session.Status is UploadSessionStatus.Open
            ? null
            : Error.Of(ErrorCodes.AlreadyFinalized, "This upload already has all its bytes.");
    }

    private static short KindValue(string kind) => (short)(kind == "file" ? ResourceKind.File : ResourceKind.Media);

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.Trim())
        {
            var code = (int)c;
            if (code is < 0x20 or (>= 0x7F and <= 0x9F) or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x200E or 0x200F) continue;
            sb.Append(c);
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s[..Math.Min(s.Length, 200)];
    }

    private static string CutUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes) break;
            sb.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return sb.ToString();
    }
}
