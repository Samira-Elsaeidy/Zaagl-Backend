using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Collaboration.Domain;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>Configuration section <c>Comments</c> (technical defaults).</summary>
public sealed class CommentOptions
{
    public const string Section = "Comments";

    /// <summary>Comments kept per resource; the next one is <c>409 limit-reached</c> until the owner deletes some.</summary>
    public int MaxPerResource { get; set; } = 500;

    public int MaxBodyLength { get; set; } = 1000;
}

/// <summary>Who is posting: the owner, a signed-in visitor or an anonymous visitor (who passed the CAPTCHA at the endpoint).</summary>
public sealed record CommentAuthor(Guid? UserId);

/// <summary>
/// E2.2 pinned comments (docs/phase2/palette-and-comments.md). Comments hang off a resource the owner opted in (image, comparison,
/// approval draft). Anyone who passes the link's gate may read them; posting needs the owner's opt-in (and, for anonymous visitors,
/// <c>allowAnonymous</c> plus the CAPTCHA checked by the endpoint). The per-resource cap holds under concurrency: the count lives on the
/// settings row and changes only under its lock. Bodies are plain text, length-capped, control characters refused; nothing here is
/// ever rendered as markup.
/// </summary>
public sealed class CommentService(CollabDbContext db, IContentPlanProvider plans, ToolOwnership ownership, TimeProvider time, IOptions<CommentOptions> options)
{
    /// <summary>Resource kinds that can carry pinned comments.</summary>
    public static readonly IReadOnlySet<ResourceKind> Targets = new HashSet<ResourceKind> { ResourceKind.Media, ResourceKind.Compare, ResourceKind.Approval };

    private int Max => Math.Max(1, options.Value.MaxPerResource);

    // ---- Owner ------------------------------------------------------------------------------------------------------------------

    public async Task<Result<CommentSettingsDto>> SetSettingsAsync(Guid resourceId, Guid userId, bool enabled, bool allowAnonymous, CancellationToken ct)
    {
        var owned = await ResolveTargetAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (enabled && !(await plans.GetForUserAsync(userId, ct)).ToolsOrNone.CommentsEnabled)
            return Error.PlanRequired("Pinned comments are not available on your plan.");
        var now = time.GetUtcNow().UtcDateTime;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO collab.comment_settings (resource_id, owner_id, enabled, allow_anonymous, comment_count, created_at, updated_at)
            VALUES ({resourceId}, {userId}, {enabled}, {allowAnonymous}, 0, {now}, {now})
            ON CONFLICT (resource_id) DO UPDATE SET enabled = EXCLUDED.enabled, allow_anonymous = EXCLUDED.allow_anonymous, updated_at = EXCLUDED.updated_at
            """, ct);
        _ = inserted;
        var row = await db.CommentSettings.AsNoTracking().FirstAsync(s => s.ResourceId == resourceId, ct);
        return new CommentSettingsDto(row.Enabled, row.AllowAnonymous, row.CommentCount, Max);
    }

    public async Task<Result<CommentThreadDto>> OwnerListAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ResolveTargetAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        return await ThreadAsync(resourceId, userId, ct);
    }

    public async Task<Result<CommentDto>> SetStatusAsync(Guid resourceId, Guid userId, long commentId, bool resolved, CancellationToken ct)
    {
        var owned = await ResolveTargetAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Comments.Where(c => c.ResourceId == resourceId && c.Id == commentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, (short)(resolved ? 1 : 0))
                .SetProperty(c => c.ResolvedAt, resolved ? now : null), ct);
        if (updated == 0) return Error.NotFound();
        var row = await db.Comments.AsNoTracking().FirstAsync(c => c.ResourceId == resourceId && c.Id == commentId, ct);
        return ToDto(row, userId);
    }

    /// <summary>Owner moderation: delete any comment of an owned resource.</summary>
    public async Task<Result<bool>> OwnerDeleteAsync(Guid resourceId, Guid userId, long commentId, CancellationToken ct)
    {
        var owned = await ResolveTargetAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        return await DeleteCoreAsync(resourceId, commentId, authorUserId: null, ct);
    }

    // ---- Link holders (the caller passed IAccessGate for the resource's code) ------------------------------------------------------

    public async Task<Result<CommentThreadDto>> ListAsync(Guid resourceId, Guid? viewer, CancellationToken ct) =>
        await ThreadAsync(resourceId, viewer, ct);

    public async Task<Result<CommentDto>> PostAsync(Guid resourceId, Guid ownerId, ResourceKind kind, CommentAuthor author, PostCommentCommand command, CancellationToken ct)
    {
        // Validate first (no I/O).
        if (!double.IsFinite(command.X) || !double.IsFinite(command.Y) || command.X is < 0 or > 1 || command.Y is < 0 or > 1)
            return Error.Validation("x and y must be between 0 and 1.");
        short? side = command.Side?.ToLowerInvariant() switch
        {
            null => null,
            "before" => 0,
            "after" => 1,
            _ => -1,
        };
        if (side == -1) return Error.Validation("side must be 'before' or 'after'.");
        if (kind == ResourceKind.Compare && side is null) return Error.Validation("A comment on a comparison needs a side ('before' or 'after').");
        if (kind != ResourceKind.Compare && side is not null) return Error.Validation("side applies to comparisons only.");
        var body = (command.Body ?? "").Trim();
        if (body.Length == 0) return Error.Validation("The comment is empty.");
        if (body.Length > options.Value.MaxBodyLength) return Error.Validation($"A comment is at most {options.Value.MaxBodyLength} characters.");
        if (body.Any(c => char.IsControl(c) && c != '\n')) return Error.Validation("A comment cannot contain control characters.");
        string? name = null;
        var isOwner = author.UserId == ownerId;
        if (!isOwner)
        {
            var cleaned = ToolOwnership.Label(command.AuthorName, 60, "authorName");
            if (!cleaned.IsSuccess) return cleaned.Error!;
            name = cleaned.Value;
        }

        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            var settings = (await db.CommentSettings
                .FromSqlInterpolated($"SELECT * FROM collab.comment_settings WHERE resource_id = {resourceId} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
            if (settings is not { Enabled: true })
            {
                await tx.RollbackAsync(CancellationToken.None);
                return Error.Forbidden("Comments are turned off for this content.");
            }
            if (author.UserId is null && !settings.AllowAnonymous)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return Error.Unauthorized("Sign in to comment on this content.");
            }
            if (settings.CommentCount >= Max)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return Error.Of(ErrorCodes.LimitReached, $"This content already has {Max} comments.");
            }
            var row = new CommentRow
            {
                ResourceId = resourceId, X = Math.Round(command.X, 4), Y = Math.Round(command.Y, 4), Side = side, Body = body,
                AuthorKind = (short)(isOwner ? 0 : author.UserId is null ? 2 : 1), AuthorUserId = author.UserId, AuthorName = name,
                Status = 0, CreatedAt = now,
            };
            db.Comments.Add(row);
            await db.SaveChangesAsync(ct);
            await db.CommentSettings.Where(s => s.ResourceId == resourceId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CommentCount, x => x.CommentCount + 1), ct);
            await tx.CommitAsync(ct);
            return ToDto(row, author.UserId);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Error.Of(ErrorCodes.Busy, "Busy. Try again in a second.");
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>A signed-in author deletes their own comment.</summary>
    public Task<Result<bool>> AuthorDeleteAsync(Guid resourceId, Guid authorUserId, long commentId, CancellationToken ct) =>
        DeleteCoreAsync(resourceId, commentId, authorUserId, ct);

    // -----------------------------------------------------------------------------------------------------------------------------

    private async Task<Result<bool>> DeleteCoreAsync(Guid resourceId, long commentId, Guid? authorUserId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.CommentSettings.FromSqlInterpolated($"SELECT * FROM collab.comment_settings WHERE resource_id = {resourceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var query = db.Comments.Where(c => c.ResourceId == resourceId && c.Id == commentId);
        if (authorUserId is { } author) query = query.Where(c => c.AuthorUserId == author);
        var deleted = await query.ExecuteDeleteAsync(ct);
        if (deleted == 0) { await tx.RollbackAsync(CancellationToken.None); return Error.NotFound(); }
        await db.CommentSettings.Where(s => s.ResourceId == resourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CommentCount, x => x.CommentCount > 0 ? x.CommentCount - 1 : 0), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<Result<CommentThreadDto>> ThreadAsync(Guid resourceId, Guid? viewer, CancellationToken ct)
    {
        var settings = await db.CommentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
        if (settings is null) return new CommentThreadDto(false, false, 0, Max, []);
        var rows = await db.Comments.AsNoTracking().Where(c => c.ResourceId == resourceId).OrderBy(c => c.Id).Take(Max).ToListAsync(ct);
        return new CommentThreadDto(settings.Enabled, settings.AllowAnonymous, settings.CommentCount, Max, rows.Select(r => ToDto(r, viewer)).ToList());
    }

    /// <summary>Owner only, and only kinds that can carry comments; anything else is 404 (PD-40).</summary>
    private async Task<Result<OwnedTool>> ResolveTargetAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        foreach (var kind in Targets)
        {
            var owned = await ownership.ResolveAsync(resourceId, userId, kind, ct);
            if (owned.IsSuccess) return owned;
        }
        return Error.NotFound();
    }

    private static CommentDto ToDto(CommentRow r, Guid? viewer) => new(
        r.Id, r.X, r.Y, r.Side switch { 0 => "before", 1 => "after", _ => null }, r.Body,
        r.AuthorKind switch { 0 => "owner", 1 => "visitor", _ => "anonymous" }, r.AuthorName,
        r.Status == 1 ? "resolved" : "open", DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
        r.ResolvedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
        viewer is not null && r.AuthorUserId == viewer);
}

/// <summary>C2 kind handler: when Resources reclaims an image, comparison or approval, its comments and settings go (cascade).</summary>
public sealed class CommentPurgeHandler(CollabDbContext db, ResourceKind kind) : IResourceKindHandler
{
    public ResourceKind Kind { get; } = kind;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.CommentSettings.Where(s => s.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
