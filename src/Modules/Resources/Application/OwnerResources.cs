using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Domain;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Resources.Application;

/// <summary>An owned resource with its authoritative state (ARC-006 §2: recomputed from the link snapshot).</summary>
public sealed record OwnedResource(ResourceDescriptor Resource, ResourceState State, LinkSnapshot? Link)
{
    /// <summary>True when the link carries no key hash (public link, PD-45).</summary>
    public bool IsPublic => Link is { LinkKeyHash: null };
}

public sealed record OwnedResourcePage(IReadOnlyList<OwnedResource> Items, string? NextCursor);

/// <param name="TitleSet">True when <c>title</c> was present in the request (so <c>null</c> clears it).</param>
public sealed record ResourcePatch(
    bool TitleSet, string? Title, DateTime? ExpiresAt, bool? Public, string? Password, bool ClearPassword, bool? BurnAfterDownload);

public sealed record ResourcePatched(OwnedResource Owned, string? LinkKey);

/// <summary>
/// F-12 (contracts.md C9): the owner side of <c>/api/resources</c> (list, detail, patch, delete). Owner only (PD-40): another
/// principal, an admin or a guest resource is <c>not_found</c>. Plan checks come from C5, link changes go through
/// <see cref="IContentLinkCommands"/> (C1); the Api composes the file and media blocks from Files.
/// </summary>
public sealed class OwnerResources(
    ResourcesDbContext db,
    ILinkSnapshots snapshots,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    TimeProvider time)
{
    private const int MaxScanBatches = 8;

    public async Task<Result<OwnedResource>> ResolveAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var row = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (row is null || row.OwnerId != userId) return Error.NotFound();
        var owned = await DescribeAsync(row, row.DownloadCount, ct);
        return owned.State == ResourceState.Purgeable ? Error.NotFound() : owned;
    }

    /// <summary>Keyset page, newest first: <c>(created_at desc, id desc)</c>. Purgeable resources never appear. The
    /// <paramref name="state"/> filter is applied to the computed state after reading, so a page may take several batches.</summary>
    public async Task<Result<OwnedResourcePage>> ListAsync(
        Guid userId, ResourceKind? kind, ResourceState? state, string? cursor, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        (DateTime CreatedAt, Guid Id)? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            after = ParseCursor(cursor);
            if (after is null) return Error.Validation("The cursor is not valid.");
        }

        var items = new List<OwnedResource>();
        string? next = null;
        for (var batch = 0; batch < MaxScanBatches && items.Count < limit; batch++)
        {
            var query = db.Resources.AsNoTracking().Where(r => r.OwnerId == userId);
            if (kind is { } k) query = query.Where(r => r.Kind == k);
            if (after is { } a) query = query.Where(r => r.CreatedAt < a.CreatedAt || (r.CreatedAt == a.CreatedAt && r.Id.CompareTo(a.Id) < 0));
            var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(limit * 2).ToListAsync(ct);
            if (rows.Count == 0) { next = null; break; }

            var consumed = 0;
            foreach (var row in rows)
            {
                consumed++;
                after = (row.CreatedAt, row.Id);
                var owned = await DescribeAsync(row, row.DownloadCount, ct);
                if (owned.State == ResourceState.Purgeable) continue;
                if (state is { } s && owned.State != s) continue;
                items.Add(owned);
                if (items.Count == limit) break;
            }
            // More rows may exist beyond what this loop consumed.
            var exhausted = rows.Count < limit * 2 && consumed == rows.Count;
            next = exhausted ? null : MakeCursor(after!.Value);
        }
        return new OwnedResourcePage(items, items.Count == 0 && next is null ? null : next);
    }

    public async Task<Result<ResourcePatched>> UpdateAsync(Guid resourceId, Guid userId, ResourcePatch patch, CancellationToken ct)
    {
        var resolved = await ResolveAsync(resourceId, userId, ct);
        if (!resolved.IsSuccess) return resolved.Error!;
        var owned = resolved.Value!;
        var resource = owned.Resource;
        if (resource.LinkId is not { } linkId) return Error.NotFound();

        var plan = await plans.GetForUserAsync(userId, ct);
        if (!plan.Capabilities.ManageContent) return Error.Forbidden("Your plan cannot edit content.");
        var now = time.GetUtcNow().UtcDateTime;

        // Validate first: nothing is changed when any field is refused.
        // Burn (ARC-019 sections 1 and 11.3): files only, Starter only, changeable only before a claim.
        if (patch.BurnAfterDownload is { } wantBurn && wantBurn != resource.BurnAfterDownload)
        {
            if (resource.BurnClaimedAt is not null || owned.State == ResourceState.Burned)
                return Error.Of(ErrorCodes.BurnAlreadyClaimed, "This content was already burned.");
            if (wantBurn && resource.Kind != ResourceKind.File) return Error.Validation("burnAfterDownload applies to files only.");
            if (wantBurn && !plan.Capabilities.BurnAfterDownload) return Error.PlanRequired("Burn after downloading is available on the Starter plan.");
        }
        if (patch.TitleSet && patch.Title is { } title && title.Trim().Length > 200) return Error.Validation("The title is at most 200 characters.");
        if (patch.Password is { } pw && (pw.Length < 4 || pw.Length > 128)) return Error.Validation("The password must be 4-128 characters.");
        if (patch.Password is not null && !plan.Capabilities.PasswordOnContent)
            return Error.PlanRequired("Password protection is available on the Starter plan.");

        DateTime? newExpiry = null;
        if (patch.ExpiresAt is { } requested)
        {
            if (owned.State == ResourceState.Burned) return Error.Of(ErrorCodes.BurnAlreadyClaimed, "This content was already burned.");
            if (owned.State is ResourceState.Deleted or ResourceState.Disabled) return Error.Conflict("This content cannot be renewed.");
            newExpiry = requested.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(requested, DateTimeKind.Utc) : requested.ToUniversalTime();
            if (newExpiry <= now) return Error.Validation("The expiry must be in the future.");
            if (newExpiry > now.AddDays(plan.Limits.RetentionDays))
                return Error.Of(ErrorCodes.ExpiryBeyondPlan, $"Your plan allows at most {plan.Limits.RetentionDays} days.");
            var current = owned.Link?.ExpiresAt ?? resource.ExpiresAt;
            if (newExpiry <= current) return Error.Validation("renewal-must-extend: the new expiry must be later than the current one.");
        }

        string? linkKey = null;
        if (newExpiry is { } expiry)
        {
            var renewed = await linkCommands.RenewAsync(linkId, expiry, ct);
            if (!renewed.IsSuccess) return renewed.Error!;
            await db.Resources.Where(r => r.Id == resourceId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LinkExpiresAt, renewed.Value!.ExpiresAt)
                .SetProperty(r => r.LinkPurgeAfter, renewed.Value.PurgeAfter)
                .SetProperty(r => r.UpdatedAt, now), ct);
        }
        if (patch.Public is { } makePublic)
        {
            if (makePublic && !owned.IsPublic)
            {
                var removed = await linkCommands.RemoveLinkKeyAsync(linkId, ct);
                if (!removed.IsSuccess) return removed.Error!;
            }
            else if (!makePublic && owned.IsPublic)
            {
                var issued = await linkCommands.IssueNewLinkKeyAsync(linkId, ct);
                if (!issued.IsSuccess) return issued.Error!;
                linkKey = issued.Value;
            }
        }
        if (patch.BurnAfterDownload is { } burnNow && burnNow != resource.BurnAfterDownload)
        {
            var changed = await db.Resources.Where(r => r.Id == resourceId && r.BurnClaimedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.BurnAfterDownload, burnNow).SetProperty(r => r.UpdatedAt, now), ct);
            if (changed == 0) return Error.Of(ErrorCodes.BurnAlreadyClaimed, "This content was already burned.");
        }
        if (patch.ClearPassword || patch.Password is not null)
        {
            var set = await linkCommands.SetPasswordAsync(linkId, patch.Password is not null ? patch.Password : null, ct);
            if (!set.IsSuccess) return set.Error!;
        }
        if (patch.TitleSet)
        {
            var cleaned = string.IsNullOrWhiteSpace(patch.Title) ? null : CleanTitle(patch.Title);
            await db.Resources.Where(r => r.Id == resourceId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Title, cleaned).SetProperty(r => r.UpdatedAt, now), ct);
        }

        var after = await ResolveAsync(resourceId, userId, ct);
        return after.IsSuccess ? new ResourcePatched(after.Value!, linkKey) : after.Error!;
    }

    /// <summary>Owner delete: the link answers 410 at once and the bytes stay until purge (PD-55). Idempotent.</summary>
    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(resourceId, userId, ct);
        if (!resolved.IsSuccess) return resolved.Error!;
        var owned = resolved.Value!;
        if (owned.State == ResourceState.Deleted) return true;
        if (owned.State == ResourceState.Disabled && owned.Link?.DisabledKind is "abuse" or "safety")
            return Error.Of(ErrorCodes.TakenDown, "This content was removed under the acceptable use policy.");
        if (owned.Resource.LinkId is not { } linkId) return Error.NotFound();
        var deleted = await linkCommands.DeleteAsync(linkId, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }

    /// <summary>Why content that is not ready cannot be ticketed: 409 processing, 410 with the specific code, or 422.</summary>
    public static Error? StateError(OwnedResource owned) => owned.State switch
    {
        ResourceState.Ready => null,
        ResourceState.Processing => Error.Of(ErrorCodes.Processing, "This content is still being prepared."),
        ResourceState.Rejected => Error.Of(ErrorCodes.ContentRejected, "This content is not available."),
        ResourceState.Deleted => Error.Of(ErrorCodes.Deleted, "This content was deleted."),
        ResourceState.Disabled => owned.Link?.DisabledKind is "abuse" or "safety"
            ? Error.Of(ErrorCodes.TakenDown, "This content was removed under the acceptable use policy.")
            : Error.Of(ErrorCodes.Disabled, "This content has been disabled."),
        ResourceState.Burned => Error.Of(ErrorCodes.Burned, "This content is gone."),
        _ => Error.Of(ErrorCodes.Expired, "This content has expired."),
    };

    // ---- internals ------------------------------------------------------------------------

    private async Task<OwnedResource> DescribeAsync(ResourceRow row, long downloads, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var descriptor = ResourceRegistry.Describe(row, downloads, now);
        LinkSnapshot? link = row.LinkId is { } linkId ? await snapshots.GetSnapshotByIdAsync(linkId, ct) : null;
        LinkState? linkState = null;
        if (link is not null)
        {
            linkState = LinkStateCalculator.Compute(link, now);
            if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        }
        var state = ResourceStateCalculator.Compute(linkState, row.ProcessingStatus, row.BurnClaimedAt, row.DeletedAt);
        return new OwnedResource(descriptor, state, link);
    }

    private static string? CleanTitle(string title)
    {
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

    private static string MakeCursor((DateTime CreatedAt, Guid Id) key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{key.CreatedAt.Ticks}|{key.Id:N}"))).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static (DateTime CreatedAt, Guid Id)? ParseCursor(string cursor)
    {
        try
        {
            if (cursor.Length > 200) return null;
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = text.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParseExact(parts[1], "N", out var id)) return null;
            return (new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
