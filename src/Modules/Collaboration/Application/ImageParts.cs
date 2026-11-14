using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Collaboration.Application;

/// <summary>What a tool link knows about one referenced image, recomputed on every read.</summary>
public sealed record ImagePart(Guid ResourceId, bool Usable, string? Mime, int? Width, int? Height, DateTime? LinkExpiresAt, bool HasThumbnail);

/// <summary>
/// Shared by comparisons and approvals (docs/phase2): the authoritative usability of a referenced image of the same owner, and the
/// inline/preview tickets for it. Usable = owned by <c>ownerId</c>, kind media, its own link active, processing ready (addendum §2.4),
/// not burn-after-download, scanned clean, detected type a raster image on the inline allowlist. Every check is fail-closed.
/// </summary>
public sealed class ImageParts(
    IResourceRegistry resources,
    ILinkSnapshots snapshots,
    IStoredObjectSummaries objects,
    IMediaMetadata media,
    IDownloadGrants grants,
    TimeProvider time)
{
    /// <summary>The inline allowlist of addendum §4.4 / ARC-008 §7 (raster images only; SVG is never inline).</summary>
    public static readonly IReadOnlySet<string> RasterTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    /// <summary>The Phase 1 thumbnail derivative kind (Files' MediaPreviewTaskHandler.DerivativeKindThumb).</summary>
    public const string ThumbDerivative = "thumb-320";

    public async Task<ImagePart> DescribeAsync(Guid resourceId, Guid ownerId, CancellationToken ct)
    {
        var unusable = new ImagePart(resourceId, false, null, null, null, null, false);
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null || resource.Kind != ResourceKind.Media || resource.Owner.UserId != ownerId || resource.BurnAfterDownload) return unusable;
        if (resource.LinkId is not { } linkId || await snapshots.GetSnapshotByIdAsync(linkId, ct) is not { } link) return unusable;

        var linkState = LinkStateCalculator.Compute(link, time.GetUtcNow().UtcDateTime);
        if (linkState == LinkState.Active && !link.IsActive) linkState = LinkState.Disabled;
        if (ResourceStateCalculator.Compute(linkState, resource.Processing, resource.BurnClaimedAt, null) != ResourceState.Ready) return unusable;

        var summary = (await objects.GetAsync([resourceId], ct)).GetValueOrDefault(resourceId);
        if (summary is not { Scan: ScanVerdict.Clean, DetectedMime: { } mime } || !RasterTypes.Contains(mime)) return unusable;

        var meta = (await media.GetAsync([resourceId], ct)).GetValueOrDefault(resourceId);
        var thumb = meta?.Derivatives.Any(d => d.Kind == ThumbDerivative && d.Status == "ready") == true;
        return new ImagePart(resourceId, true, mime, meta?.Width, meta?.Height, link.ExpiresAt, thumb);
    }

    /// <summary>For creation: usable or <c>422 resource-unusable</c> (also for someone else's or a missing resource: no existence oracle).</summary>
    public async Task<Result<ImagePart>> RequireAsync(Guid resourceId, Guid ownerId, string role, CancellationToken ct)
    {
        if (resourceId == Guid.Empty) return Error.Validation($"{role} is required.");
        var part = await DescribeAsync(resourceId, ownerId, ct);
        return part.Usable
            ? part
            : Error.Of(ErrorCodes.ResourceUnusable, $"The {role} image cannot be used: it must be one of your images (PNG, JPEG, GIF or WebP), scanned, ready and not expired.");
    }

    /// <summary>The descriptor block of a part: inline ticket on the original and a preview ticket on the thumbnail when ready.</summary>
    public async Task<ImagePartDto> ToDtoAsync(ImagePart part, string? label, CancellationToken ct)
    {
        if (!part.Usable) return new ImagePartDto(label, "unavailable", null, null, null, null, null);
        var inline = await grants.MintAsync(new MintTicketRequest(part.ResourceId, DeliveryPurpose.Inline, null), ct);
        if (!inline.IsSuccess) return new ImagePartDto(label, "unavailable", null, null, null, null, null);
        string? preview = null;
        if (part.HasThumbnail)
        {
            var thumb = await grants.MintAsync(new MintTicketRequest(part.ResourceId, DeliveryPurpose.Preview, ThumbDerivative), ct);
            if (thumb.IsSuccess) preview = thumb.Value!.Url;
        }
        return new ImagePartDto(label, "ready", part.Mime, part.Width, part.Height, inline.Value!.Url, preview);
    }
}
