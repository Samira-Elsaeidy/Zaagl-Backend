using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Collaboration.Domain;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>An owned tool resource with its link and its authoritative resource state (lower-case name).</summary>
public sealed record OwnedTool(ResourceDescriptor Resource, LinkSnapshot Link, string State);

/// <summary>Shared owner resolution and label rules of the Collaboration tool links.</summary>
public sealed class ToolOwnership(IResourceRegistry resources, ILinkSnapshots snapshots, TimeProvider time)
{
    /// <summary>Owner only (PD-40): another owner's, a guest's, another kind's or a purgeable resource is 404 with no difference.</summary>
    public async Task<Result<OwnedTool>> ResolveAsync(Guid resourceId, Guid userId, ResourceKind kind, CancellationToken ct)
    {
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind != kind || resource.Owner.UserId != userId || resource.LinkId is not { } linkId) return Error.NotFound();
        var link = await snapshots.GetSnapshotByIdAsync(linkId, ct);
        if (link is null) return Error.NotFound();
        var linkState = LinkStateCalculator.Compute(link, time.GetUtcNow().UtcDateTime);
        if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        var state = ResourceStateCalculator.Compute(linkState, resource.Processing, resource.BurnClaimedAt, null);
        if (state == ResourceState.Purgeable) return Error.NotFound();
        var name = state == ResourceState.Disabled && link.DisabledKind is "abuse" or "safety" ? "taken-down" : state.ToString().ToLowerInvariant();
        return new OwnedTool(resource, link, name);
    }

    /// <summary>Plain-text label: trimmed, at most <paramref name="max"/> characters, no control characters. Null/blank = none.</summary>
    public static Result<string?> Label(string? raw, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Result<string?>.Ok(null);
        var label = raw.Trim();
        if (label.Length > max) return Error.Validation($"{field} is at most {max} characters.");
        if (label.Any(char.IsControl)) return Error.Validation($"{field} cannot contain control characters.");
        return Result<string?>.Ok(label);
    }

    /// <summary>The content expiry rule (addendum §9) further capped by <paramref name="caps"/> (the referenced parts' expiries).</summary>
    public static Result<DateTime> Expiry(DateTime? requested, DateTime now, int retentionDays, IEnumerable<DateTime?> caps)
    {
        var max = now.AddDays(retentionDays);
        DateTime expiresAt;
        if (requested is { } r)
        {
            expiresAt = r.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(r, DateTimeKind.Utc) : r.ToUniversalTime();
            if (expiresAt <= now.AddMinutes(1)) return Error.Validation("The expiry must be at least a minute in the future.");
            if (expiresAt > max) return Error.Of(ErrorCodes.ExpiryBeyondPlan, $"Your plan allows at most {retentionDays} days.");
        }
        else
        {
            expiresAt = max;
        }
        foreach (var cap in caps)
            if (cap is { } c && c < expiresAt) expiresAt = DateTime.SpecifyKind(c, DateTimeKind.Utc);
        if (expiresAt <= now.AddMinutes(1)) return Error.Of(ErrorCodes.ResourceUnusable, "A referenced image expires too soon.");
        return expiresAt;
    }
}

/// <summary>
/// E2.2 before/after slider (docs/phase2/compare-slider.md). A comparison is a content link of kind <c>compare</c> whose row points
/// at two usable images of the same owner. Create order (addendum §2.2): plan, parts, register (no bytes, no quota), row, link (C1),
/// bind, ready.
/// </summary>
public sealed class CompareService(
    CollabDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    ImageParts parts,
    ToolOwnership ownership,
    TimeProvider time,
    ILogger<CompareService> logger)
{
    public const int MaxLabelLength = 40;

    public async Task<Result<ToolLinkCreatedDto>> CreateAsync(Guid userId, CreatedVia via, CreateComparisonCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = await plans.GetForUserAsync(userId, ct);
        if (!plan.ToolsOrNone.CompareEnabled) return Error.PlanRequired("Before/after comparisons are not available on your plan.");

        var beforeLabel = ToolOwnership.Label(command.BeforeLabel, MaxLabelLength, "beforeLabel");
        if (!beforeLabel.IsSuccess) return beforeLabel.Error!;
        var afterLabel = ToolOwnership.Label(command.AfterLabel, MaxLabelLength, "afterLabel");
        if (!afterLabel.IsSuccess) return afterLabel.Error!;
        var title = ToolOwnership.Label(command.Title, 200, "title");
        if (!title.IsSuccess) return title.Error!;
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");
        if (command.BeforeResourceId == command.AfterResourceId && command.BeforeResourceId != Guid.Empty)
            return Error.Validation("The before and after images must be different.");

        var before = await parts.RequireAsync(command.BeforeResourceId, userId, "before", ct);
        if (!before.IsSuccess) return before.Error!;
        var after = await parts.RequireAsync(command.AfterResourceId, userId, "after", ct);
        if (!after.IsSuccess) return after.Error!;

        var expiry = ToolOwnership.Expiry(command.ExpiresAt, now, plan.Limits.RetentionDays, [before.Value!.LinkExpiresAt, after.Value!.LinkExpiresAt]);
        if (!expiry.IsSuccess) return expiry.Error!;

        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Compare, new ContentOwner(userId, null), 0, expiry.Value, title.Value, BurnAfterDownload: false, via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;
        db.Comparisons.Add(new ComparisonRow
        {
            ResourceId = resourceId, OwnerId = userId, BeforeResourceId = command.BeforeResourceId, AfterResourceId = command.AfterResourceId,
            BeforeLabel = beforeLabel.Value, AfterLabel = afterLabel.Value, CreatedAt = now,
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

        var issued = await issuer.CreateContentLinkAsync(new ContentLinkRequest(
            resourceId, LinkTargetKind.Compare, userId, null, expiry.Value, command.CustomSlug, title.Value, command.Password,
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
        return new ToolLinkCreatedDto(resourceId, link.Code, link.ShortUrl, link.LinkKey, link.ExpiresAt);
    }

    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.Comparisons.Where(c => c.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up comparison resource {ResourceId} after a failed create", resourceId);
        }
    }

    public Task<ComparisonRow?> GetRowAsync(Guid resourceId, CancellationToken ct) =>
        db.Comparisons.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == resourceId, ct);

    /// <summary>Public descriptor (the caller has passed the gate for this code).</summary>
    public async Task<Result<CompareDescriptorDto>> DescribeAsync(Guid resourceId, LinkSnapshot link, string? title, CancellationToken ct)
    {
        var row = await GetRowAsync(resourceId, ct);
        if (row is null) return Error.NotFound();
        var (before, after) = await PartsAsync(row, ct);
        var state = before.State == "ready" && after.State == "ready" ? "ready" : "incomplete";
        return new CompareDescriptorDto("compare", state, title, link.ExpiresAt ?? default, link.HasPassword, before, after);
    }

    public async Task<(ImagePartDto Before, ImagePartDto After)> PartsAsync(ComparisonRow row, CancellationToken ct)
    {
        var before = await parts.ToDtoAsync(await parts.DescribeAsync(row.BeforeResourceId, row.OwnerId, ct), row.BeforeLabel, ct);
        var after = await parts.ToDtoAsync(await parts.DescribeAsync(row.AfterResourceId, row.OwnerId, ct), row.AfterLabel, ct);
        return (before, after);
    }

    public async Task<Result<OwnerComparisonDto>> GetOwnedAsync(Guid resourceId, Guid userId, string baseUrl, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Compare, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var row = await GetRowAsync(resourceId, ct);
        if (row is null) return Error.NotFound();
        var o = owned.Value!;
        var (before, after) = await PartsAsync(row, ct);
        return new OwnerComparisonDto(resourceId, o.Link.Slug, $"{baseUrl.TrimEnd('/')}/{o.Link.Slug}", o.Resource.Title, o.State,
            o.Link.ExpiresAt ?? o.Resource.ExpiresAt, o.Link.LinkKeyHash is null, o.Link.HasPassword, row.BeforeResourceId, row.AfterResourceId,
            before, after, DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    public async Task<Result<bool>> UpdateLabelsAsync(Guid resourceId, Guid userId, string? beforeLabel, bool beforeSet, string? afterLabel, bool afterSet, bool swap, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Compare, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var b = ToolOwnership.Label(beforeLabel, MaxLabelLength, "beforeLabel");
        if (!b.IsSuccess) return b.Error!;
        var a = ToolOwnership.Label(afterLabel, MaxLabelLength, "afterLabel");
        if (!a.IsSuccess) return a.Error!;
        var row = await GetRowAsync(resourceId, ct);
        if (row is null) return Error.NotFound();
        var newBefore = beforeSet ? b.Value : row.BeforeLabel;
        var newAfter = afterSet ? a.Value : row.AfterLabel;
        var beforeId = row.BeforeResourceId;
        var afterId = row.AfterResourceId;
        if (swap)
        {
            (beforeId, afterId) = (afterId, beforeId);
            (newBefore, newAfter) = (newAfter, newBefore);
        }
        await db.Comparisons.Where(c => c.ResourceId == resourceId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.BeforeLabel, newBefore).SetProperty(c => c.AfterLabel, newAfter)
            .SetProperty(c => c.BeforeResourceId, beforeId).SetProperty(c => c.AfterResourceId, afterId), ct);
        return true;
    }

    /// <summary>Owner delete (PD-55 semantics): the link answers 410; the referenced images are untouched.</summary>
    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ownership.ResolveAsync(resourceId, userId, ResourceKind.Compare, ct);
        if (!owned.IsSuccess) return owned.Error!;
        if (owned.Value!.State == "deleted") return true;
        if (owned.Value.State == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This comparison was removed under the acceptable use policy.");
        var deleted = await linkCommands.DeleteAsync(owned.Value.Link.Id, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }
}

/// <summary>C2 kind handler: Resources reclaims a comparison (purge, orphan) and its row goes. The images are separate resources.</summary>
public sealed class ComparisonKindHandler(CollabDbContext db) : IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Compare;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.Comparisons.Where(c => c.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
