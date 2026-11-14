using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.DeveloperTools.Contracts;
using Zaagl.Modules.DeveloperTools.Domain;
using Zaagl.Modules.DeveloperTools.Infrastructure;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.DeveloperTools.Application;

/// <summary>The recording of a capsule as the reader sees it now.</summary>
public sealed record CapsuleRecording(Guid ResourceId, bool Usable, string? Mime, long? SizeBytes, DateTime? LinkExpiresAt);

/// <summary>
/// E2.1 bug capsules (docs/phase2/bug-capsules.md): a content link of kind <c>capsule</c> bundling an optional screen recording (an
/// existing video upload of the same owner, never copied) with a strict, redacted console and network log document. Create order
/// (addendum §2.2): plan, logs, recording, register (logs bytes reserved against the quota), row, link (C1), bind, ready.
/// </summary>
public sealed class CapsuleService(
    DevToolsDbContext db,
    IResourceRegistry resources,
    ILinkIssuer issuer,
    ILinkSnapshots snapshots,
    IContentLinkCommands linkCommands,
    IContentPlanProvider plans,
    IStoredObjectSummaries objects,
    IDownloadGrants grants,
    TimeProvider time,
    ILogger<CapsuleService> logger)
{
    public async Task<Result<CapsuleCreatedDto>> CreateAsync(Guid userId, CreatedVia via, CreateCapsuleCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plan = await plans.GetForUserAsync(userId, ct);
        var tools = plan.ToolsOrNone;
        if (!tools.CapsulesEnabled) return Error.PlanRequired("Bug capsules are not available on your plan.");

        if (command.Logs.ValueKind != JsonValueKind.Object) return Error.Validation("logs is required (a JSON object, schema version 1).");
        var rawBytes = Encoding.UTF8.GetByteCount(command.Logs.GetRawText());
        if (rawBytes > tools.CapsuleMaxLogBytes)
            return Error.Of(ErrorCodes.LogsTooLarge, $"The logs can be at most {tools.CapsuleMaxLogBytes} bytes on your plan.");
        var normalized = CapsuleLogs.Normalize(command.Logs);
        if (!normalized.IsSuccess) return normalized.Error!;
        var (json, summary) = normalized.Value;
        var logsBytes = Encoding.UTF8.GetByteCount(json);

        string? title = null;
        if (!string.IsNullOrWhiteSpace(command.Title))
        {
            title = command.Title.Trim();
            if (title.Length > 200 || title.Any(char.IsControl)) return Error.Validation("The title is at most 200 characters, without control characters.");
        }
        if (command.Password is { } pw)
        {
            if (pw.Length < 4 || pw.Length > 128) return Error.Validation("The password must be 4-128 characters.");
            if (!plan.Capabilities.PasswordOnContent) return Error.PlanRequired("Password protection is available on the Starter plan.");
        }
        if (command.CustomSlug is { } slug && (slug.Trim().Length is < 4 or > 64)) return Error.Validation("The alias must be 4-64 characters.");

        DateTime? recordingExpiry = null;
        if (command.RecordingResourceId is { } recordingId)
        {
            var recording = await RecordingAsync(recordingId, userId, ct);
            if (!recording.Usable)
                return Error.Of(ErrorCodes.ResourceUnusable, "The recording cannot be used: it must be one of your MP4 or WebM uploads, scanned, ready and not expired.");
            recordingExpiry = recording.LinkExpiresAt;
        }

        var maxExpiry = now.AddDays(plan.Limits.RetentionDays);
        var expiresAt = maxExpiry;
        if (command.ExpiresAt is { } requested)
        {
            expiresAt = requested.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(requested, DateTimeKind.Utc) : requested.ToUniversalTime();
            if (expiresAt <= now.AddMinutes(1)) return Error.Validation("The expiry must be at least a minute in the future.");
            if (expiresAt > maxExpiry) return Error.Of(ErrorCodes.ExpiryBeyondPlan, $"Your plan allows at most {plan.Limits.RetentionDays} days.");
        }
        if (recordingExpiry is { } cap && cap < expiresAt) expiresAt = DateTime.SpecifyKind(cap, DateTimeKind.Utc);

        var registered = await resources.RegisterAsync(new RegisterResourceCommand(
            ResourceKind.Capsule, new ContentOwner(userId, null), logsBytes, expiresAt, title, BurnAfterDownload: false, via), ct);
        if (!registered.IsSuccess) return registered.Error!;
        var resourceId = registered.Value!.ResourceId;
        db.Capsules.Add(new CapsuleRow
        {
            ResourceId = resourceId, OwnerId = userId, RecordingResourceId = command.RecordingResourceId, Logs = json, LogsBytes = logsBytes,
            ConsoleCount = summary.ConsoleCount, ErrorCount = summary.ErrorCount, NetworkCount = summary.NetworkCount,
            FailedRequestCount = summary.FailedRequestCount, CreatedAt = now,
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
            resourceId, LinkTargetKind.Capsule, userId, null, expiresAt, command.CustomSlug, title, command.Password,
            RequireLinkKey: !command.Public), ct);
        if (!issued.IsSuccess)
        {
            await AbortAsync(resourceId);
            return issued.Error!;
        }
        var link = issued.Value!;
        var bound = await resources.BindLinkAsync(resourceId, link.LinkId, ct);
        if (!bound.IsSuccess) return bound.Error!;
        var ready = await resources.MarkReadyAsync(resourceId, logsBytes, ct);
        if (!ready.IsSuccess) return ready.Error!;
        return new CapsuleCreatedDto(resourceId, link.Code, link.ShortUrl, link.LinkKey, link.ExpiresAt);
    }

    private async Task AbortAsync(Guid resourceId)
    {
        try
        {
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.CreateFailed, CancellationToken.None);
            await db.Capsules.Where(c => c.ResourceId == resourceId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up capsule resource {ResourceId} after a failed create", resourceId);
        }
    }

    /// <summary>Usable = owned by <paramref name="ownerId"/>, file or media, its own link active, processing ready, not burn, scanned clean,
    /// and validated as video (viewer <c>video</c>, MP4 or WebM).</summary>
    public async Task<CapsuleRecording> RecordingAsync(Guid resourceId, Guid ownerId, CancellationToken ct)
    {
        var unusable = new CapsuleRecording(resourceId, false, null, null, null);
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind is not (ResourceKind.File or ResourceKind.Media) || resource.Owner.UserId != ownerId || resource.BurnAfterDownload)
            return unusable;
        if (resource.LinkId is not { } linkId || await snapshots.GetSnapshotByIdAsync(linkId, ct) is not { } link) return unusable;
        var linkState = LinkStateCalculator.Compute(link, time.GetUtcNow().UtcDateTime);
        if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        if (ResourceStateCalculator.Compute(linkState, resource.Processing, resource.BurnClaimedAt, null) != ResourceState.Ready) return unusable;
        var summary = (await objects.GetAsync([resourceId], ct)).GetValueOrDefault(resourceId);
        if (summary is not { Scan: ScanVerdict.Clean, Viewer: "video", DetectedMime: { } mime }) return unusable;
        return new CapsuleRecording(resourceId, true, mime, summary.SizeBytes, link.ExpiresAt);
    }

    private async Task<CapsuleRecordingDto?> RecordingDtoAsync(CapsuleRow row, CancellationToken ct)
    {
        if (row.RecordingResourceId is not { } recordingId) return null;
        var recording = await RecordingAsync(recordingId, row.OwnerId, ct);
        if (!recording.Usable) return new CapsuleRecordingDto("unavailable", null, null, null);
        var ticket = await grants.MintAsync(new MintTicketRequest(recordingId, DeliveryPurpose.Inline, null), ct);
        return ticket.IsSuccess
            ? new CapsuleRecordingDto("ready", recording.Mime, recording.SizeBytes, ticket.Value!.Url)
            : new CapsuleRecordingDto("unavailable", null, null, null);
    }

    private static CapsuleSummaryDto Summary(CapsuleRow row) =>
        new(row.ConsoleCount, row.ErrorCount, row.NetworkCount, row.FailedRequestCount, row.LogsBytes);

    /// <summary>Public descriptor (the caller passed the gate for this code).</summary>
    public async Task<Result<CapsuleDescriptorDto>> DescribeAsync(Guid resourceId, LinkSnapshot link, string? title, CancellationToken ct)
    {
        var row = await db.Capsules.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        using var doc = JsonDocument.Parse(row.Logs);
        return new CapsuleDescriptorDto("capsule", title, link.ExpiresAt ?? default, link.HasPassword, await RecordingDtoAsync(row, ct),
            Summary(row), doc.RootElement.Clone());
    }

    public async Task<Result<OwnerCapsuleDto>> GetOwnedAsync(Guid resourceId, Guid userId, string baseUrl, CancellationToken ct)
    {
        var owned = await ResolveOwnedAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var (resource, link, state) = owned.Value;
        var row = await db.Capsules.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == resourceId, ct);
        if (row is null) return Error.NotFound();
        return new OwnerCapsuleDto(resourceId, link.Slug, $"{baseUrl.TrimEnd('/')}/{link.Slug}", resource.Title, state,
            link.ExpiresAt ?? resource.ExpiresAt, link.LinkKeyHash is null, link.HasPassword, row.RecordingResourceId,
            await RecordingDtoAsync(row, ct), Summary(row), DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    public async Task<Result<bool>> DeleteAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var owned = await ResolveOwnedAsync(resourceId, userId, ct);
        if (!owned.IsSuccess) return owned.Error!;
        var (_, link, state) = owned.Value;
        if (state == "deleted") return true;
        if (state == "taken-down") return Error.Of(ErrorCodes.TakenDown, "This capsule was removed under the acceptable use policy.");
        var deleted = await linkCommands.DeleteAsync(link.Id, ct);
        return deleted.IsSuccess || deleted.Error!.Code == ErrorCodes.NotFound ? true : deleted.Error!;
    }

    private async Task<Result<(ResourceDescriptor Resource, LinkSnapshot Link, string State)>> ResolveOwnedAsync(Guid resourceId, Guid userId, CancellationToken ct)
    {
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind != ResourceKind.Capsule || resource.Owner.UserId != userId || resource.LinkId is not { } linkId) return Error.NotFound();
        var link = await snapshots.GetSnapshotByIdAsync(linkId, ct);
        if (link is null) return Error.NotFound();
        var linkState = LinkStateCalculator.Compute(link, time.GetUtcNow().UtcDateTime);
        if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        var state = ResourceStateCalculator.Compute(linkState, resource.Processing, resource.BurnClaimedAt, null);
        if (state == ResourceState.Purgeable) return Error.NotFound();
        var name = state == ResourceState.Disabled && link.DisabledKind is "abuse" or "safety" ? "taken-down" : state.ToString().ToLowerInvariant();
        return (resource, link, name);
    }
}

/// <summary>C2 kind handler: Resources reclaims a capsule (purge, orphan) and its row goes. The recording is a separate resource.</summary>
public sealed class CapsuleKindHandler(DevToolsDbContext db) : IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Capsule;

    public Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        db.Capsules.Where(c => c.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
