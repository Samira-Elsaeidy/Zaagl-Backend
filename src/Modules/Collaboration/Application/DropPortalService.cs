using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Collaboration.Domain;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>What the upload endpoints need to know about a portal to admit (or continue) one upload.</summary>
/// <param name="MaxFileBytes">Effective: the portal's per-file cap further capped by the owner's current plan.</param>
/// <param name="MaxPerHour">The owner's current plan value (all uploaders of this portal together).</param>
public sealed record DropAdmission(
    Guid PortalId, Guid OwnerId, string? Title, int MaxFiles, long MaxBytes, long MaxFileBytes, int MaxPerHour,
    IReadOnlyList<string> AllowedTypes, bool RequireName, bool RequireEmail, bool NotifyOwner);

/// <summary>The uploader's optional identification at <c>/complete</c>, validated.</summary>
public sealed record DropUploader(string? Name, string? Email, string? Note);

/// <summary>A notice the caller should queue (at most one per portal per <see cref="DropPortalService.NoticeWindow"/>).</summary>
public sealed record DropNotice(Guid OwnerId, Guid PortalId, string? Title, int NewFiles);

/// <summary>
/// E2.3 "Reverse Pigeon" drop-off portals (docs/phase2/drop-portals.md). A portal is a content link of kind <c>drop</c>
/// (<c>zaa.gl/drop/&lt;code&gt;</c>) whose row holds the owner's settings. People without an account upload into it with tus; the files
/// become ordinary File/Media resources of the owner (quota reserved atomically per upload, scanned before the owner can download),
/// attributed here. The live-portal cap per owner is checked under a per-owner transaction advisory lock; the per-portal file, byte and
/// hourly caps are enforced by Files under a per-portal advisory lock (UploadService).
/// </summary>
public sealed class DropPortalService(
    CollabDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    IDropUploadUsage usage,
    IStoredObjectSummaries objects,
    ToolOwnership ownership,
    TimeProvider time,
    ILogger<DropPortalService> logger)
{
    public const int MaxInstructions = 2000;
    public const int MaxListed = 500;
    public static readonly TimeSpan NoticeWindow = TimeSpan.FromMinutes(15);

    public async Task<Result<ToolLinkCreatedDto>> CreateAsync(Guid userId, CreatedVia via, CreateDropPortalCommand command, string shortBase, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = await plans.GetForUserAsync(userId, ct);
        var tools = plan.ToolsOrNone;
        if (!tools.DropEnabled || tools.DropMaxActive < 1) return Error.PlanRequired("Drop-off portals are not available on your plan.");

        var title = ToolOwnership.Label(command.Title, 200, "title");
        if (!title.IsSuccess) return title.Error!;
        var instructions = Instructions(command.Instructions);
        if (!instructions.IsSuccess) return instructions.Error!;
        var types = Types(command.AllowedTypes);
        if (!types.IsSuccess) return types.Error!;
        var caps = Caps(command.MaxFiles ?? tools.DropMaxFiles, command.MaxBytes ?? tools.DropMaxBytes,
            command.MaxFileBytes ?? plan.Limits.MaxFileBytes, tools, plan.Limits.MaxFileBytes);
        if (!caps.IsSuccess) return caps.Error!;
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");
        var expiry = ToolOwnership.Expiry(command.ExpiresAt, now, plan.Limits.RetentionDays, []);
        if (!expiry.IsSuccess) return expiry.Error!;
        var (maxFiles, maxBytes, maxFileBytes) = caps.Value;

        // The live-portal cap: counted and the row inserted under one per-owner transaction lock (concurrent creates cannot both pass).
        Guid resourceId;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockOwnerAsync(userId, ct);
            if (await CountLiveAsync(userId, now, ct) >= tools.DropMaxActive)
                return Error.Of(ErrorCodes.LimitReached, $"Your plan allows {tools.DropMaxActive} open drop-off portals. Close one first.");

            var registered = await resources.RegisterAsync(new RegisterResourceCommand(
                ResourceKind.Drop, new ContentOwner(userId, null), 0, expiry.Value, title.Value, BurnAfterDownload: false, via), ct);
            if (!registered.IsSuccess) return registered.Error!;
            resourceId = registered.Value!.ResourceId;
            db.DropPortals.Add(new DropPortalRow
            {
                ResourceId = resourceId, OwnerId = userId, Instructions = instructions.Value, MaxFiles = maxFiles, MaxBytes = maxBytes,
                MaxFileBytes = maxFileBytes, AllowedTypes = string.Join(',', types.Value!), RequireName = command.RequireName,
                RequireEmail = command.RequireEmail, NotifyOwner = command.NotifyOwner, CreatedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                db.ChangeTracker.Clear();
                await AbortAsync(resourceId);
                throw;
            }
        }

        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Drop, userId, null, expiry.Value, command.CustomSlug, title.Value, command.Password,
            RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
            return issued.Error!;
        }
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        var ready = await resources.MarkReadyAsync(resourceId, 0, ct);
        if (!ready.IsSuccess) return ready.Error!;
        return new ToolLinkCreatedDto(resourceId, link.Code, DropUrl(shortBase, link.Code), link.LinkKey, link.ExpiresAt);
    }

    /// <summary><c>zaa.gl/drop/&lt;code&gt;</c> (the short host also answers <c>zaa.gl/&lt;code&gt;</c> with the same viewer).</summary>
    public static string DropUrl(string shortBase, string code) => $"{shortBase.TrimEnd('/')}/drop/{code}";

    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.DropPortals.Where(p => p.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up drop portal resource {ResourceId} after a failed create", resourceId);
        }
    }

    private Task LockOwnerAsync(Guid userId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", ["zaagl.drop-owner:" + userId.ToString("D")], ct);

    /// <summary>Open portals: not closed and their resource still live (not expired, deleted, disabled or purgeable).</summary>
    private async Task<int> CountLiveAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var open = await db.DropPortals.AsNoTracking().Where(p => p.OwnerId == userId && p.ClosedAt == null)
            .Select(p => p.ResourceId).ToListAsync(ct);
        var live = 0;
        foreach (var id in open)
        {
            // A portal being created right now has no link yet (no expiry known): it counts, so a parallel create cannot slip past.
            // (until its link is bound the registry reports it purgeable; a failed create is marked rejected and stops counting).
            var resource = await resources.GetAsync(id, ct);
            if (resource is null) continue;
            if (resource.LinkId is null)
            {
                if (resource.Processing != ProcessingStatus.Rejected) live++;
                continue;
            }
            if (resource.ExpiresAt > now && resource.State is ResourceState.Ready or ResourceState.Processing) live++;
        }
        return live;
    }

    // ---- Validation ----------------------------------------------------------------------------------------------------------------

    private static Result<string?> Instructions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Result<string?>.Ok(null);
        var text = raw.Trim();
        if (text.Length > MaxInstructions) return Error.Validation($"instructions is at most {MaxInstructions} characters.");
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return Error.Validation("instructions cannot contain control characters.");
        return Result<string?>.Ok(text);
    }

    private static Result<IReadOnlyList<string>> Types(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return Result<IReadOnlyList<string>>.Ok([]);
        var list = new List<string>();
        foreach (var t in raw)
        {
            var v = (t ?? "").Trim().ToLowerInvariant();
            if (!UploadTypeCategories.All.Contains(v)) return Error.Validation($"allowedTypes accepts {string.Join(", ", UploadTypeCategories.All)}.");
            if (!list.Contains(v)) list.Add(v);
        }
        return Result<IReadOnlyList<string>>.Ok(list);
    }

    private static Result<(int, long, long)> Caps(int maxFiles, long maxBytes, long maxFileBytes, ToolLimits tools, long planFileBytes)
    {
        if (maxFiles < 1 || maxFiles > tools.DropMaxFiles) return Error.Validation($"maxFiles must be between 1 and {tools.DropMaxFiles} on your plan.");
        if (maxBytes < 1 || maxBytes > tools.DropMaxBytes) return Error.Validation($"maxBytes must be between 1 and {tools.DropMaxBytes} on your plan.");
        if (maxFileBytes < 1 || maxFileBytes > planFileBytes) return Error.Validation($"maxFileBytes must be between 1 and {planFileBytes} on your plan.");
        return (maxFiles, maxBytes, Math.Min(maxFileBytes, maxBytes));
    }

    public static IReadOnlyList<string> SplitTypes(string value) =>
        value.Length == 0 ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    // ---- Owner ---------------------------------------------------------------------------------------------------------------------

    public async Task<Result<OwnerDropPortalDto>> GetOwnedAsync(Guid resourceId, Guid userId, string shortBase, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Drop, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var row = await db.DropPortals.AsNoTracking().FirstOrDefaultAsync(p => p.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        return await ToOwnerDtoAsync(owned.Value!, row, shortBase, ct);
    }

    public async Task<IReadOnlyList<OwnerDropPortalDto>> ListAsync(Guid userId, string shortBase, CancellationToken ct)
    {
        var rows = await db.DropPortals.AsNoTracking().Where(p => p.OwnerId == userId)
            .OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        var list = new List<OwnerDropPortalDto>();
        foreach (var row in rows)
        {
            var owned = await ownership.ResolveAsync(row.ResourceId, userId, ResourceKind.Drop, ct);
            if (!owned.IsSuccess || owned.Value!.State == "deleted") continue;
            list.Add(await ToOwnerDtoAsync(owned.Value, row, shortBase, ct));
        }
        return list;
    }

    private async Task<OwnerDropPortalDto> ToOwnerDtoAsync(OwnedTool o, DropPortalRow row, string shortBase, CancellationToken ct)
    {
        var used = await usage.GetAsync(row.ResourceId, ct);
        var accepting = o.State == "ready" && row.ClosedAt is null && used.Files < row.MaxFiles && used.Bytes < row.MaxBytes;
        return new OwnerDropPortalDto(row.ResourceId, o.Link.Slug, DropUrl(shortBase, o.Link.Slug), o.Resource.Title, o.State, accepting,
            Utc(row.ClosedAt), o.Link.ExpiresAt ?? o.Resource.ExpiresAt, o.Link.LinkKeyHash is null, o.Link.HasPassword, row.Instructions,
            row.MaxFiles, row.MaxBytes, row.MaxFileBytes, SplitTypes(row.AllowedTypes), row.RequireName, row.RequireEmail, row.NotifyOwner,
            used.Files, used.Bytes, DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    public async Task<Result<bool>> UpdateAsync(Guid resourceId, Guid userId, UpdateDropPortalCommand command, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Drop, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State is "deleted" or "taken-down") return Error.Conflict("This portal can no longer be changed.");
        var row = await db.DropPortals.FirstOrDefaultAsync(p => p.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var plan = await plans.GetForUserAsync(userId, ct);
        if (command.Instructions is not null)
        {
            var instructions = Instructions(command.Instructions);
            if (!instructions.IsSuccess) return instructions.Error!;
            row.Instructions = instructions.Value;
        }
        if (command.AllowedTypes is not null)
        {
            var types = Types(command.AllowedTypes);
            if (!types.IsSuccess) return types.Error!;
            row.AllowedTypes = string.Join(',', types.Value!);
        }
        if (command.MaxFiles is not null || command.MaxBytes is not null || command.MaxFileBytes is not null)
        {
            var caps = Caps(command.MaxFiles ?? row.MaxFiles, command.MaxBytes ?? row.MaxBytes, command.MaxFileBytes ?? row.MaxFileBytes,
                plan.ToolsOrNone, plan.Limits.MaxFileBytes);
            if (!caps.IsSuccess) return caps.Error!;
            (row.MaxFiles, row.MaxBytes, row.MaxFileBytes) = caps.Value;
        }
        if (command.RequireName is { } rn) row.RequireName = rn;
        if (command.RequireEmail is { } re) row.RequireEmail = re;
        if (command.NotifyOwner is { } no) row.NotifyOwner = no;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Close (no new uploads; uploads already admitted may finish) or reopen (subject to the live-portal cap again).</summary>
    public async Task<Result<bool>> SetClosedAsync(Guid resourceId, Guid userId, bool closed, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Drop, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State != "ready") return Error.Conflict("Only a live portal can be closed or reopened.");
        var now = time.GetUtcNow().UtcDateTime;
        if (closed)
        {
            await db.DropPortals.Where(p => p.ResourceId == resourceId && p.ClosedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosedAt, now), ct);
            return true;
        }
        var tools = (await plans.GetForUserAsync(userId, ct)).ToolsOrNone;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwnerAsync(userId, ct);
        var isClosed = await db.DropPortals.AnyAsync(p => p.ResourceId == resourceId && p.ClosedAt != null, ct);
        if (!isClosed) return true;
        if (await CountLiveAsync(userId, now, ct) >= tools.DropMaxActive)
            return Error.Of(ErrorCodes.LimitReached, $"Your plan allows {tools.DropMaxActive} open drop-off portals. Close one first.");
        await db.DropPortals.Where(p => p.ResourceId == resourceId && p.ClosedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosedAt, (DateTime?)null), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Drop, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State == "deleted") return true;
        if (owned.Value.State == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This portal was removed under the acceptable use policy.");
        var deleted = await linkCommands.DeleteAsync(owned.Value.Link.Id, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }

    /// <summary>The received files, newest first (at most <see cref="MaxListed"/>). Owner only.</summary>
    public async Task<Result<IReadOnlyList<DropFileDto>>> FilesAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Drop, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var rows = await db.DropFiles.AsNoTracking().Where(f => f.PortalId == resourceId && f.OwnerId == userId)
            .OrderByDescending(f => f.ReceivedAt).Take(MaxListed).ToListAsync(ct);
        var summaries = await objects.GetAsync(rows.Select(r => r.ResourceId).ToList(), ct);
        var list = new List<DropFileDto>(rows.Count);
        foreach (var r in rows)
        {
            var resource = await resources.GetAsync(r.ResourceId, ct);
            if (resource is null || resource.Owner.UserId != userId || resource.State == ResourceState.Purgeable) continue;
            var summary = summaries.GetValueOrDefault(r.ResourceId);
            list.Add(new DropFileDto(r.ResourceId, summary?.DisplayName, summary?.SizeBytes ?? resource.SizeBytes, summary?.DetectedMime,
                (summary?.Scan ?? ScanVerdict.Pending).ToString().ToLowerInvariant(), resource.State.ToString().ToLowerInvariant(),
                r.UploaderName, r.UploaderEmail, r.Note, DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc)));
        }
        return list;
    }

    // ---- Uploader (the gate already passed for this code) --------------------------------------------------------------------------

    public async Task<Result<DropDescriptorDto>> DescribeAsync(Guid resourceId, LinkSnapshot link, string? title, CancellationToken ct)
    {
        var admission = await AdmitAsync(resourceId, forNewUpload: false, ct);
        if (!admission.IsSuccess) return admission.Error!;
        var a = admission.Value!;
        var row = await db.DropPortals.AsNoTracking().FirstAsync(p => p.ResourceId == resourceId, ct);
        var state = "open";
        if (row.ClosedAt is not null) state = "closed";
        else
        {
            var used = await usage.GetAsync(resourceId, ct);
            if (used.Files >= a.MaxFiles || used.Bytes >= a.MaxBytes) state = "full";
        }
        return new DropDescriptorDto("drop", state, title, row.Instructions, link.ExpiresAt ?? default, link.HasPassword, a.MaxFileBytes,
            a.AllowedTypes, a.RequireName, a.RequireEmail);
    }

    /// <summary>The portal's admission data. <paramref name="forNewUpload"/>: a closed portal or an owner whose plan lost the feature
    /// refuses with <c>409 drop-closed</c>; uploads already admitted may still be resumed and finished.</summary>
    public async Task<Result<DropAdmission>> AdmitAsync(Guid resourceId, bool forNewUpload, CancellationToken ct)
    {
        var row = await db.DropPortals.AsNoTracking().FirstOrDefaultAsync(p => p.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Owner.UserId != row.OwnerId) return Error.NotFound();
        var plan = await plans.GetForUserAsync(row.OwnerId, ct);
        var tools = plan.ToolsOrNone;
        if (forNewUpload && (row.ClosedAt is not null || !tools.DropEnabled))
            return Error.Of(ErrorCodes.DropClosed, "This portal is not accepting uploads.");
        return new DropAdmission(row.ResourceId, row.OwnerId, resource.Title, row.MaxFiles, row.MaxBytes,
            Math.Min(row.MaxFileBytes, plan.Limits.MaxFileBytes), Math.Max(1, tools.DropUploadsPerHour), SplitTypes(row.AllowedTypes),
            row.RequireName, row.RequireEmail, row.NotifyOwner);
    }

    /// <summary>Validates the uploader's identification against the portal's requirements (before the upload is finished).</summary>
    public static Result<DropUploader> Uploader(DropAdmission portal, string? name, string? email, string? note)
    {
        var n = ToolOwnership.Label(name, 100, "name");
        if (!n.IsSuccess) return n.Error!;
        if (portal.RequireName && n.Value is null) return Error.Validation("This portal asks for your name.");
        string? e = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            e = email.Trim();
            var at = e.IndexOf('@');
            if (e.Length > 254 || at < 1 || at != e.LastIndexOf('@') || at == e.Length - 1 || e.IndexOf('.', at) < 0
                || e.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                return Error.Validation("The email address is not valid.");
        }
        if (portal.RequireEmail && e is null) return Error.Validation("This portal asks for your email address.");
        var text = (note ?? "").Trim();
        if (text.Length > 1000 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            return Error.Validation("The note is at most 1000 characters, without control characters.");
        return new DropUploader(n.Value, e, text.Length == 0 ? null : text);
    }

    /// <summary>
    /// Records one received file (idempotent per resource: a replayed <c>/complete</c> records nothing new) and decides whether the owner
    /// should be emailed now: at most one notice per portal per <see cref="NoticeWindow"/> (a conditional UPDATE on the last notice time,
    /// so concurrent uploads queue one notice, not several).
    /// </summary>
    public async Task<DropNotice?> RecordFileAsync(DropAdmission portal, Guid fileResourceId, Guid uploadId, DropUploader uploader, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (!await db.DropFiles.AnyAsync(f => f.ResourceId == fileResourceId, ct))
        {
            db.DropFiles.Add(new DropFileRow
            {
                ResourceId = fileResourceId, PortalId = portal.PortalId, OwnerId = portal.OwnerId, UploadSessionId = uploadId,
                UploaderName = uploader.Name, UploaderEmail = uploader.Email, Note = uploader.Note, ReceivedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // a parallel replay recorded it first
                return null;
            }
        }
        else
        {
            return null;
        }

        if (!portal.NotifyOwner) return null;
        var previous = await db.DropPortals.AsNoTracking().Where(p => p.ResourceId == portal.PortalId).Select(p => p.LastNotifiedAt).FirstOrDefaultAsync(ct);
        if (previous is { } last && now - last < NoticeWindow) return null;
        var won = await db.DropPortals
            .Where(p => p.ResourceId == portal.PortalId && p.NotifyOwner && p.LastNotifiedAt == previous)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastNotifiedAt, now), ct);
        if (won == 0) return null;
        var since = previous ?? DateTime.MinValue;
        var count = await db.DropFiles.CountAsync(f => f.PortalId == portal.PortalId && f.ReceivedAt > since, ct);
        return new DropNotice(portal.OwnerId, portal.PortalId, portal.Title, Math.Max(1, count));
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}

/// <summary>C2 kind handler: Resources reclaims a portal (purge, orphan): its row and attribution rows go. The received files are
/// separate resources of the owner and stay in the library on their own lifecycle.</summary>
public sealed class DropPortalKindHandler(CollabDbContext db) : IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Drop;

    public async Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        await db.DropFiles.Where(f => f.PortalId == resourceId).ExecuteDeleteAsync(cancellationToken);
        await db.DropPortals.Where(p => p.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>C2 kind handler for received files: when a file resource is reclaimed, its attribution row (uploader name, email, note) goes too.</summary>
public sealed class DropFilePurgeHandler(CollabDbContext db, ResourceKind kind) : IResourceKindHandler
{
    public ResourceKind Kind => kind;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.DropFiles.Where(f => f.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
