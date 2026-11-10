using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

/// <summary>
/// C1 (ARC-002 §5): owner operations on an existing content link. The caller (Snippets now, Resources later) has
/// already checked ownership and the plan; these methods only touch content links and answer <c>not_found</c> for a
/// URL link, a deleted link or an unknown id. Each runs in one transaction, writes its C10 outbox row in it, and
/// evicts the cache after the commit.
/// </summary>
public sealed partial class LinkService : IContentLinkCommands
{
    private async Task<Link?> LoadContentLinkAsync(Guid linkId, CancellationToken ct) =>
        await db.Links.Include(l => l.Policy)
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TargetKind != LinkTargetKind.Url && l.DeletedAt == null, ct);

    private void Emit(Link link, LifecycleEventType type, DateTime now, object? payload = null) =>
        db.LifecycleOutbox.Add(new LifecycleOutboxRow
        {
            LinkId = link.Id, ResourceId = link.ResourceId!.Value, Kind = link.TargetKind, Event = type,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload), OccurredAt = now,
        });

    public async Task<Result<string>> IssueNewLinkKeyAsync(Guid linkId, CancellationToken cancellationToken)
    {
        var link = await LoadContentLinkAsync(linkId, cancellationToken);
        if (link?.Policy is null) return Error.NotFound("Link not found.");
        var raw = RandomNumberGenerator.GetBytes(16);
        link.Policy.LinkKeyHash = SHA256.HashData(raw);
        link.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await EvictAfterCommitAsync(link.SlugKey);
        return Base64Url.EncodeToString(raw);
    }

    public async Task<Result<bool>> RemoveLinkKeyAsync(Guid linkId, CancellationToken cancellationToken)
    {
        var link = await LoadContentLinkAsync(linkId, cancellationToken);
        if (link?.Policy is null) return Error.NotFound("Link not found.");
        link.Policy.LinkKeyHash = null;
        link.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await EvictAfterCommitAsync(link.SlugKey);
        return true;
    }

    public async Task<Result<bool>> SetPasswordAsync(Guid linkId, string? password, CancellationToken cancellationToken)
    {
        if (password is { } pw && (pw.Length < 4 || pw.Length > 128)) return Error.Validation("The password must be 4-128 characters.");
        var link = await LoadContentLinkAsync(linkId, cancellationToken);
        if (link?.Policy is null) return Error.NotFound("Link not found.");
        link.Policy.PasswordHash = string.IsNullOrEmpty(password) ? null : BCrypt.Net.BCrypt.HashPassword(password);
        link.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await EvictAfterCommitAsync(link.SlugKey);
        return true;
    }

    public async Task<Result<ContentLinkRenewed>> RenewAsync(Guid linkId, DateTime newExpiresAt, CancellationToken cancellationToken)
    {
        var link = await LoadContentLinkAsync(linkId, cancellationToken);
        if (link?.Policy is null) return Error.NotFound("Link not found.");
        var now = time.GetUtcNow().UtcDateTime;
        var state = LinkStateCalculator.Compute(link.PurgeAfter, link.DeletedAt, link.DisabledAt, link.Policy.ExpiresAt, now);
        if (state == LinkState.Purgeable) return Error.NotFound("Link not found.");
        if (state is LinkState.Deleted or LinkState.Disabled) return Error.Conflict("This link cannot be renewed.");

        var expiry = newExpiresAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(newExpiresAt, DateTimeKind.Utc) : newExpiresAt.ToUniversalTime();
        if (expiry <= now) return Error.Validation("The new expiry must be in the future.");
        if (link.Policy.ExpiresAt is { } current && expiry <= current)
            return Error.Validation("renewal-must-extend: the new expiry must be later than the current one.");

        var purgeAfter = link.OwnerId is null ? expiry : expiry.AddDays(options.Value.QuarantineDays);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        link.Policy.ExpiresAt = expiry;
        link.PurgeAfter = purgeAfter;
        link.UpdatedAt = now;
        link.ExpiringNoticeQueuedAt = null;
        link.ExpiredNoticeQueuedAt = null;
        link.PurgeNoticeQueuedAt = null;
        Emit(link, LifecycleEventType.Renewed, now, new { ExpiresAt = expiry, PurgeAfter = purgeAfter });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await EvictAfterCommitAsync(link.SlugKey);
        return new ContentLinkRenewed(link.Id, expiry, purgeAfter);
    }

    /// <summary>Soft delete as spec §4.3 (the alias stays reserved until purge_after, which only moves earlier).
    /// Deleting an already deleted link is not found here; callers treat that as done.</summary>
    public async Task<Result<bool>> DeleteAsync(Guid linkId, CancellationToken cancellationToken)
    {
        var link = await LoadContentLinkAsync(linkId, cancellationToken);
        if (link is null) return Error.NotFound("Link not found.");
        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        link.DeletedAt = now;
        var shortened = now.AddDays(options.Value.QuarantineDays);
        link.PurgeAfter = link.PurgeAfter is { } existing && existing < shortened ? existing : shortened;
        link.UpdatedAt = now;
        Emit(link, LifecycleEventType.Deleted, now);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await EvictAfterCommitAsync(link.SlugKey);
        return true;
    }
}
