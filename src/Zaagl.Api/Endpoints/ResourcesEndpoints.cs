using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Resources.Application;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi ScanDto (owner only).</summary>
public sealed record ScanDto(string Status, DateTime? ScannedAt);

/// <summary>openapi MediaDto. <c>mediaType</c>, <c>detectedMime</c>, <c>browserPlayable</c> always; <c>width</c>, <c>height</c> (displayed, after EXIF
/// orientation), <c>frameCount</c>, <c>hasAlpha</c>, <c>orientation</c> and <c>derivatives</c> once the <c>media-preview</c> task probed the original (S3,
/// ARC-011 section 3.1); null until then, and for an image the probe could not read. <c>hasLocationData</c> stays null (PD-13 OPEN).</summary>
public sealed record MediaBlockDto(
    string MediaType,
    string DetectedMime,
    int? Width,
    int? Height,
    int? DurationMs,
    int? FrameCount,
    bool? HasAlpha,
    string? ColorSpace,
    string? IccProfileName,
    int? Orientation,
    bool BrowserPlayable,
    bool? HasLocationData,
    IReadOnlyList<MediaDerivativeDto> Derivatives);

/// <summary>openapi MediaDerivativeDto. <c>url</c> is a Preview ticket (15 minutes), only for a ready derivative of a non-burn file.</summary>
public sealed record MediaDerivativeDto(string Kind, string Status, int? Width, int? Height, string? Url);

public sealed record ResourceFileBlockDto(string Filename, string DetectedMime, string? Sha256);

/// <summary>openapi ResourceSummary plus the ResourceDetail fields (absent from a list item when null).</summary>
public sealed record ResourceDto(
    Guid Id,
    string Kind,
    string? Title,
    string Code,
    string ShortUrl,
    string State,
    string ProcessingStatus,
    string? Filename,
    string? Mime,
    string? ThumbnailUrl,
    long SizeBytes,
    DateTime ExpiresAt,
    DateTime? PurgeAfter,
    bool BurnAfterDownload,
    bool IsPublic,
    long DownloadCount,
    DateTime CreatedAt,
    string? RejectedReason = null,
    bool? HasPassword = null,
    DateTime? BurnClaimedAt = null,
    string? LinkKey = null,
    ScanDto? Scan = null,
    MediaBlockDto? Media = null,
    ResourceFileBlockDto? File = null);

public sealed record ResourceStatusDto(string State, string ProcessingStatus, string? RejectedReason, IReadOnlyList<MediaDerivativeDto> Derivatives);

public sealed record DownloadTicketDto(string Url, DateTime ExpiresAt);

public sealed record StorageLimitsDto(long MaxFileBytes, long MaxSnippetBytes, int RetentionDays, bool QuarantineAfterExpiry);

/// <summary>S2 (additive): what the plan may do, so the uploader can hide burn and password before the user tries.</summary>
public sealed record StorageCapabilitiesDto(bool BurnAfterDownload, bool PasswordOnContent);

public sealed record StorageDto(
    StorageLimitsDto Limits, long UsedBytes, long ReservedBytes, long? QuotaBytes, int OpenUploads, StorageCapabilitiesDto? Capabilities = null);

/// <summary>
/// The owner library and detail (F-12, ARC-006, ARC-009): <c>/api/resources</c> (list, detail, status, patch, delete, owner
/// inline and download tickets) and <c>/api/me/storage</c>. Owner only (PD-40): another principal, an admin or a guest is 404.
/// The link key is never returned here, except once in the PATCH answer that issued a new one.
/// </summary>
public static class ResourcesEndpoints
{
    public static void MapResources(this IEndpointRouteBuilder app)
    {
        var resources = app.MapGroup("/api/resources").WithTags("Resources");

        resources.MapGet("", async (
                string? kind, string? state, string? cursor, int? limit, ClaimsPrincipal user, HttpContext http,
                OwnerResources owner, IStoredObjectSummaries objects, IMediaMetadata mediaMeta, IDownloadGrants grants,
                IOptions<Zaagl.Modules.Links.Application.LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                ResourceKind? kindFilter = null;
                if (!string.IsNullOrEmpty(kind))
                {
                    if (!TryKind(kind, out var k)) return ApiResults.Problem(Error.Validation("Unknown kind."));
                    kindFilter = k;
                }
                ResourceState? stateFilter = null;
                if (!string.IsNullOrEmpty(state))
                {
                    if (!Enum.TryParse<ResourceState>(state, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                        return ApiResults.Problem(Error.Validation("Unknown state."));
                    stateFilter = s;
                }
                if (limit is < 1 or > 100) return ApiResults.Problem(Error.Validation("limit is 1 to 100."));

                var page = await owner.ListAsync(user.GetUserId(), kindFilter, stateFilter, cursor, limit ?? 25, ct);
                if (!page.IsSuccess) return ApiResults.Problem(page.Error!);
                var summaries = await objects.GetAsync(page.Value!.Items.Select(i => i.Resource.ResourceId).ToList(), ct);
                var metas = await mediaMeta.GetAsync(page.Value.Items.Where(i => i.Resource.Kind == ResourceKind.Media).Select(i => i.Resource.ResourceId).ToList(), ct);
                var items = new List<ResourceDto>();
                foreach (var i in page.Value.Items)
                {
                    var meta = metas.GetValueOrDefault(i.Resource.ResourceId);
                    // A short-lived Preview ticket on the thumb-320 derivative (never for burn files, never when not ready).
                    var thumbUrl = await ThumbnailTicketAsync(grants, i, meta, ct);
                    items.Add(ToDto(i, summaries.GetValueOrDefault(i.Resource.ResourceId), links.Value.BaseUrl, detail: false, meta, thumbUrl));
                }
                return Results.Ok(new { items, nextCursor = page.Value.NextCursor });
            })
            .RequireScope(ApiScopes.ResourcesRead);

        resources.MapGet("/{id:guid}", async (
                Guid id, ClaimsPrincipal user, HttpContext http, OwnerResources owner, IStoredObjectSummaries objects, IMediaMetadata mediaMeta, IDownloadGrants grants,
                IOptions<Zaagl.Modules.Links.Application.LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var resolved = await owner.ResolveAsync(id, user.GetUserId(), ct);
                if (!resolved.IsSuccess) return ApiResults.Problem(resolved.Error!);
                var summary = (await objects.GetAsync([id], ct)).GetValueOrDefault(id);
                var meta = (await mediaMeta.GetAsync([id], ct)).GetValueOrDefault(id);
                return Results.Ok(ToDto(resolved.Value!, summary, links.Value.BaseUrl, detail: true, meta, await ThumbnailTicketAsync(grants, resolved.Value!, meta, ct)));
            })
            .RequireScope(ApiScopes.ResourcesRead);

        // PATCH: title, expiresAt (renewal), public, password. The kind's write scope applies (interim rule, contracts.md C9).
        resources.MapPatch("/{id:guid}", async (
                Guid id, JsonElement body, ClaimsPrincipal user, HttpContext http, OwnerResources owner, IStoredObjectSummaries objects, IMediaMetadata mediaMeta, IDownloadGrants grants,
                IOptions<Zaagl.Modules.Links.Application.LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var userId = user.GetUserId();
                var resolved = await owner.ResolveAsync(id, userId, ct);
                if (!resolved.IsSuccess) return ApiResults.Problem(resolved.Error!);
                if (KindScopeError(user, resolved.Value!.Resource.Kind) is { } scopeError) return scopeError;

                var patch = ParsePatch(body);
                if (!patch.IsSuccess) return ApiResults.Problem(patch.Error!);
                var updated = await owner.UpdateAsync(id, userId, patch.Value!, ct);
                if (!updated.IsSuccess) return ApiResults.Problem(updated.Error!);
                var summary = (await objects.GetAsync([id], ct)).GetValueOrDefault(id);
                var meta = (await mediaMeta.GetAsync([id], ct)).GetValueOrDefault(id);
                return Results.Ok(ToDto(updated.Value!.Owned, summary, links.Value.BaseUrl, detail: true, meta, await ThumbnailTicketAsync(grants, updated.Value.Owned, meta, ct)) with { LinkKey = updated.Value.LinkKey });
            })
            .RequireScope(ApiScopes.FilesWrite)
            .RequireRateLimiting("content-write");

        resources.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, OwnerResources owner, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                var resolved = await owner.ResolveAsync(id, userId, ct);
                if (!resolved.IsSuccess) return ApiResults.Problem(resolved.Error!);
                if (KindScopeError(user, resolved.Value!.Resource.Kind) is { } scopeError) return scopeError;
                var deleted = await owner.DeleteAsync(id, userId, ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.FilesWrite)
            .RequireRateLimiting("content-write");

        resources.MapGet("/{id:guid}/status", async (
                Guid id, ClaimsPrincipal user, HttpContext http, OwnerResources owner, TimeProvider time, IMediaMetadata mediaMeta, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var resolved = await owner.ResolveAsync(id, user.GetUserId(), ct);
                if (!resolved.IsSuccess) return ApiResults.Problem(resolved.Error!);
                var o = resolved.Value!;
                if (o.State == ResourceState.Processing)
                {
                    // 1 s for the first 10 s, then 3 s (BUILD-ORDER 4).
                    var age = time.GetUtcNow().UtcDateTime - o.Resource.CreatedAt;
                    http.Response.Headers.RetryAfter = (age < TimeSpan.FromSeconds(10) ? 1 : 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                var derivatives = o.Resource.Kind == ResourceKind.Media && o.State == ResourceState.Ready
                    ? (await mediaMeta.GetAsync([id], ct)).GetValueOrDefault(id)?.Derivatives.Select(d => new MediaDerivativeDto(d.Kind, d.Status, d.Width, d.Height, null)).ToList() ?? []
                    : [];
                return Results.Ok(new ResourceStatusDto(Lower(o.State), Lower(o.Resource.Processing), o.Resource.RejectedReason, derivatives));
            })
            .RequireScope(ApiScopes.ResourcesRead);

        // Owner view ticket for the original (Inline, 15 minutes). Never consumes burn; refused for burn files and for types
        // that are not on the inline allowlist.
        resources.MapPost("/{id:guid}/inline", async (
                Guid id, ClaimsPrincipal user, HttpContext http, OwnerResources owner, IStoredObjectSummaries objects,
                IDownloadGrants grants, IOptions<ContentOptions> content, IMediaMetadata inlineMeta, IOptions<MediaOptions> mediaOptions, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var resolved = await owner.ResolveAsync(id, user.GetUserId(), ct);
                if (!resolved.IsSuccess) return ApiResults.Problem(resolved.Error!);
                var o = resolved.Value!;
                if (o.Resource.Kind == ResourceKind.Snippet) return ApiResults.Problem(Error.Conflict("Snippets have no inline view."));
                if (OwnerResources.StateError(o) is { } stateError) return ApiResults.Problem(stateError);
                if (o.Resource.BurnAfterDownload) return ApiResults.Problem(Error.Conflict("Burn files have no inline view."));
                var summary = (await objects.GetAsync([id], ct)).GetValueOrDefault(id);
                if (summary?.DetectedMime is not { } mime || !content.Value.InlineMimeAllowlist.Contains(mime, StringComparer.OrdinalIgnoreCase))
                    return ApiResults.Problem(Error.Conflict("This file type does not open inline."));
                if (o.Resource.Kind == ResourceKind.Media
                    && ExceedsInlinePixels((await inlineMeta.GetAsync([id], ct)).GetValueOrDefault(id), mediaOptions.Value))
                    return ApiResults.Problem(Error.Conflict("This image is too large to open inline. Download it instead."));
                var ticket = await grants.MintAsync(new MintTicketRequest(id, DeliveryPurpose.Inline, null), ct);
                return ticket.IsSuccess ? Results.Ok(new DownloadTicketDto(ticket.Value!.Url, ticket.Value.ExpiresAt)) : ApiResults.Problem(ticket.Error!);
            })
            .RequireScope(ApiScopes.ResourcesRead);

        // Owner download ticket (purpose Owner, attachment). Never consumes burn and never counts as a download.
        resources.MapPost("/{id:guid}/download", async (
                Guid id, ClaimsPrincipal user, HttpContext http, OwnerResources owner, IDownloadGrants grants, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var resolved = await owner.ResolveAsync(id, user.GetUserId(), ct);
                if (!resolved.IsSuccess) return ApiResults.Problem(resolved.Error!);
                var o = resolved.Value!;
                if (o.Resource.Kind == ResourceKind.Snippet) return ApiResults.Problem(Error.Conflict("Download a snippet through its raw ticket."));
                if (OwnerResources.StateError(o) is { } stateError) return ApiResults.Problem(stateError);
                var ticket = await grants.MintAsync(new MintTicketRequest(id, DeliveryPurpose.Owner, null), ct);
                return ticket.IsSuccess ? Results.Ok(new DownloadTicketDto(ticket.Value!.Url, ticket.Value.ExpiresAt)) : ApiResults.Problem(ticket.Error!);
            })
            .RequireScope(ApiScopes.ResourcesRead);

        app.MapGet("/api/me/storage", async (
                ClaimsPrincipal user, HttpContext http, IContentPlanProvider plans, IContentQuota quota, IUploadStats stats, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var userId = user.GetUserId();
                var plan = await plans.GetForUserAsync(userId, ct);
                var usage = await quota.GetUsageAsync(userId, ct);
                return Results.Ok(new StorageDto(
                    new StorageLimitsDto(plan.Limits.MaxFileBytes, plan.Limits.MaxSnippetBytes, plan.Limits.RetentionDays, plan.Limits.QuarantineAfterExpiry),
                    usage.UsedBytes, usage.ReservedBytes, usage.QuotaBytes, await stats.CountOpenAsync(userId, ct),
                    new StorageCapabilitiesDto(plan.Capabilities.BurnAfterDownload, plan.Capabilities.PasswordOnContent)));
            })
            .WithTags("Account")
            .RequireScope(ApiScopes.ResourcesRead);
    }

    /// <summary>The kind's write scope (snippets:write for snippets) on top of files:write for an API key; a session JWT has every
    /// scope. Interim rule (contracts.md C9): a separate resources:write scope is OPEN (ARC-014 Q3).</summary>
    private static IResult? KindScopeError(ClaimsPrincipal user, ResourceKind kind) =>
        kind == ResourceKind.Snippet && !ApiScopes.Allows(user, ApiScopes.SnippetsWrite) ? ApiScopes.InsufficientScope(ApiScopes.SnippetsWrite) : null;

    /// <summary>True when the probe measured more pixels than <c>Media:MaxInlinePixels</c>: the original is then download-only (ARC-011 section 6).</summary>
    internal static bool ExceedsInlinePixels(StoredMedia? meta, MediaOptions options) =>
        meta is { Width: { } w, Height: { } h } && (long)w * h > options.MaxInlinePixels;

    /// <summary>The thumb-320 ticket for one row: only for a ready derivative of a ready, non-burn media resource.</summary>
    internal static async Task<string?> ThumbnailTicketAsync(IDownloadGrants grants, OwnedResource owned, StoredMedia? meta, CancellationToken ct)
    {
        var r = owned.Resource;
        if (r.Kind != ResourceKind.Media || r.BurnAfterDownload || owned.State != ResourceState.Ready || meta is null) return null;
        if (!meta.Derivatives.Any(d => d.Kind == MediaPreviewTaskHandler.DerivativeKindThumb && d.Status == "ready")) return null;
        var ticket = await grants.MintAsync(new MintTicketRequest(r.ResourceId, DeliveryPurpose.Preview, MediaPreviewTaskHandler.DerivativeKindThumb), ct);
        return ticket.IsSuccess ? ticket.Value!.Url : null;
    }

    internal static ResourceDto ToDto(OwnedResource owned, StoredObjectSummary? stored, string baseUrl, bool detail, StoredMedia? media = null, string? thumbnailUrl = null)
    {
        var r = owned.Resource;
        var code = owned.Link?.Slug ?? "";
        var isFile = r.Kind is ResourceKind.File or ResourceKind.Media;
        var dto = new ResourceDto(
            r.ResourceId, Lower(r.Kind), r.Title, code, $"{baseUrl.TrimEnd('/')}/{code}", Lower(owned.State), Lower(r.Processing),
            isFile ? stored?.DisplayName : null, isFile ? stored?.DetectedMime : null, thumbnailUrl,
            r.SizeBytes, owned.Link?.ExpiresAt ?? r.ExpiresAt, owned.Link?.PurgeAfter ?? r.PurgeAfter, r.BurnAfterDownload, owned.IsPublic,
            r.DownloadCount, r.CreatedAt, r.RejectedReason, BurnClaimedAt: r.BurnClaimedAt); // S2: both also in the list summary
        // The list carries the media block only once the probe ran (dimensions and the thumbnail for the library); the detail always has it.
        if (!detail) return media is not null && r.Kind == ResourceKind.Media && stored?.DetectedMime is { } listMime ? dto with { Media = MediaBlock(listMime, media, thumbnailUrl) } : dto;
        return dto with
        {
            HasPassword = owned.Link?.HasPassword ?? false,
            Scan = isFile && stored is not null ? new ScanDto(Lower(stored.Scan), stored.ScannedAt) : null,
            Media = r.Kind == ResourceKind.Media && stored?.DetectedMime is { } mime ? MediaBlock(mime, media, thumbnailUrl) : null,
            File = isFile && stored is not null ? new ResourceFileBlockDto(stored.DisplayName, stored.DetectedMime ?? "application/octet-stream", stored.Sha256) : null,
        };
    }

    /// <summary>The <c>media</c> block (ARC-011 section 3.1): the type from the detected MIME, plus the probe result and the derivatives once the
    /// <c>media-preview</c> task has run. An image it could not read keeps null dimensions and the viewer relies on the browser.</summary>
    internal static MediaBlockDto MediaBlock(string detectedMime, StoredMedia? meta = null, string? thumbnailUrl = null) =>
        new(meta?.Animated == true ? "animatedImage" : "image", detectedMime, meta?.Width, meta?.Height, null, meta?.FrameCount, meta?.HasAlpha, null, null,
            meta?.Orientation, BrowserPlayable: true, HasLocationData: null,
            Derivatives: meta?.Derivatives.Select(d => new MediaDerivativeDto(d.Kind, d.Status, d.Width, d.Height,
                d.Kind == MediaPreviewTaskHandler.DerivativeKindThumb ? thumbnailUrl : null)).ToList() ?? []);

    private static Result<ResourcePatch> ParsePatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return Error.Validation("The body must be a JSON object.");
        var titleSet = false;
        string? title = null;
        DateTime? expiresAt = null;
        bool? isPublic = null;
        string? password = null;
        var clearPassword = false;
        bool? burn = null;
        foreach (var property in body.EnumerateObject())
        {
            var v = property.Value;
            switch (property.Name)
            {
                case "title":
                    if (v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return Error.Validation("title must be a string or null.");
                    titleSet = true;
                    title = v.ValueKind == JsonValueKind.Null ? null : v.GetString();
                    break;
                case "expiresAt":
                    if (v.ValueKind == JsonValueKind.Null) break;
                    if (v.ValueKind != JsonValueKind.String || !v.TryGetDateTime(out var parsed)) return Error.Validation("expiresAt is not a valid date-time.");
                    expiresAt = parsed;
                    break;
                case "public":
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) isPublic = v.GetBoolean();
                    else if (v.ValueKind != JsonValueKind.Null) return Error.Validation("public must be a boolean.");
                    break;
                case "password":
                    if (v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return Error.Validation("password must be a string or null.");
                    password = v.ValueKind == JsonValueKind.Null ? null : v.GetString();
                    break;
                case "clearPassword":
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) clearPassword = v.GetBoolean();
                    else if (v.ValueKind != JsonValueKind.Null) return Error.Validation("clearPassword must be a boolean.");
                    break;
                case "burnAfterDownload":
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) burn = v.GetBoolean();
                    else if (v.ValueKind != JsonValueKind.Null) return Error.Validation("burnAfterDownload must be a boolean.");
                    break;
            }
        }
        return new ResourcePatch(titleSet, title, expiresAt, isPublic, password, clearPassword, burn);
    }

    private static bool TryKind(string value, out ResourceKind kind)
    {
        kind = default;
        return value.ToLowerInvariant() switch
        {
            "snippet" => (kind = ResourceKind.Snippet) == ResourceKind.Snippet,
            "file" => (kind = ResourceKind.File) == ResourceKind.File,
            "media" => (kind = ResourceKind.Media) == ResourceKind.Media,
            _ => false,
        };
    }

    internal static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
