using Zaagl.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Resources.Application;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi ContentDescriptor. For a snippet: <c>snippet</c>, <c>rawUrl</c> and <c>sizeBytes</c> only when the state is
/// <c>ready</c>; <c>inlineUrl</c>, <c>previewUrl</c>, <c>media</c>, <c>file</c>, <c>mime</c> and <c>filename</c> are always null.</summary>
public sealed record ContentDescriptorDto(
    string Kind,
    string State,
    string? Title,
    long? SizeBytes,
    string? Mime,
    string? Filename,
    DateTime ExpiresAt,
    bool BurnAfterDownload,
    bool HasPassword,
    string? InlineUrl,
    string? PreviewUrl,
    string? RawUrl,
    SnippetViewDto? Snippet,
    object? Media,
    object? File);

/// <summary>openapi ContentDescriptor.file (S2). No URL of any kind.</summary>
public sealed record FileBlockDto(string DetectedMime, long SizeBytes, string? Sha256, long? SingleResponseLimitBytes);

public sealed record UnlockContentRequest(string? Password);

public sealed record UnlockContentResponse(string Grant, DateTime ExpiresAt);

/// <summary>
/// H3 (ARC-003 §4): the public content API on <c>api.zaa.gl</c>. The viewer reaches it through the BFF; the CLI directly.
/// The gate (<see cref="IAccessGate"/>) runs on every call; nothing is consumed and no analytics event is written
/// until PD-10.
/// </summary>
public static class PublicContentEndpoints
{
    public static void MapPublicContent(this IEndpointRouteBuilder app)
    {
        var content = app.MapGroup("/api/public/content").WithTags("Public content").AllowAnonymous();

        content.MapGet("/{code}", async (
                string code, HttpContext http, IAccessGate gate, ILinkResolver links, IResourceRegistry registry,
                ISnippetReader reader, IDownloadGrants grants, IStoredObjectSummaries objects, IOptions<ContentOptions> content,
                CancellationToken ct) =>
            {
                // ARC-003 §4.1: private, no-store and noindex on every answer, errors included.
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers["X-Robots-Tag"] = "noindex";

                var credentials = new AccessCredentials(Header(http, AccessWireNames.LinkKeyHeader), Header(http, AccessWireNames.GrantHeader));
                var gated = await gate.EvaluateAsync(code, credentials, AccessPurpose.View, ct);
                if (gated.Outcome != AccessGateOutcome.Allowed) return ApiResults.Problem(ContentAccessGate.ToError(gated));

                // Phase 1 slice: snippets and media (S1) have a descriptor; files (S2) answer like an unknown code.
                if (gated.Kind is not (LinkTargetKind.Snippet or LinkTargetKind.Media or LinkTargetKind.File) || gated.ResourceId is not { } resourceId) return ApiResults.NotFound();
                var kindName = gated.Kind switch { LinkTargetKind.Media => "media", LinkTargetKind.File => "file", _ => "snippet" };
                var resource = await registry.GetAsync(resourceId, ct);
                if (resource is null) return ApiResults.NotFound();
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);

                // ARC-006 §2: recompute from the authoritative link state the gate saw.
                var state = ResourceStateCalculator.Compute(gated.LinkState, resource.Processing, resource.BurnClaimedAt, null);
                switch (state)
                {
                    case ResourceState.Rejected: return ApiResults.Problem(Error.Of(ErrorCodes.ContentRejected, "This content is not available."));
                    case ResourceState.Burned: return ApiResults.Problem(Error.Of(ErrorCodes.Burned, "This content is gone."));
                    case ResourceState.Ready or ResourceState.Processing: break;
                    default: return ApiResults.Problem(Error.Of(ErrorCodes.Expired, "This content has expired."));
                }

                var expiresAt = link?.ExpiresAt ?? resource.ExpiresAt;
                var hasPassword = link?.HasPassword ?? false;
                if (state == ResourceState.Processing)
                    return Results.Ok(new ContentDescriptorDto(kindName, "processing", resource.Title, null, null, null, expiresAt,
                        false, hasPassword, null, null, null, null, null, null));

                if (gated.Kind == LinkTargetKind.File)
                    return await FileDescriptorAsync(resource, expiresAt, hasPassword, objects, content.Value, ct);

                if (gated.Kind == LinkTargetKind.Media)
                    return await MediaDescriptorAsync(resource, expiresAt, hasPassword, objects, content.Value, grants, ct);

                var view = await reader.GetViewAsync(resourceId, null, ct);
                if (view is null) return ApiResults.NotFound();
                // Interim (ARC-003 Q2): visitors see the latest version only.
                var visible = view with { Versions = [view.Version], ExpiresAt = expiresAt };
                var ticket = await grants.MintAsync(new MintTicketRequest(resourceId, DeliveryPurpose.Raw, $"v{view.Version}"), ct);
                if (!ticket.IsSuccess) return ApiResults.Problem(ticket.Error!);

                return Results.Ok(new ContentDescriptorDto("snippet", "ready", view.Title, view.SizeBytes, null, null, expiresAt,
                    false, hasPassword, null, null, ticket.Value!.Url, visible, null, null));
            })
            .RequireRateLimiting("content-read");

        content.MapPost("/{code}/unlock", async (
                string code, UnlockContentRequest request, HttpContext http, IAccessGrants grants, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                if (string.IsNullOrEmpty(request.Password)) return ApiResults.Problem(Error.Validation("A password is required."));
                var unlocked = await grants.UnlockAsync(code, request.Password, Header(http, AccessWireNames.LinkKeyHeader), ct);
                return ApiResults.From(unlocked, g => Results.Ok(new UnlockContentResponse(g.Token, g.ExpiresAt)));
            })
            .RequireRateLimiting("unlock");

        // The download button (ARC-008 section 2): only a POST mints a download ticket (valid 120 s if unused). S1: images, no burn.
        content.MapPost("/{code}/download", async (
                string code, HttpContext http, IAccessGate gate, IResourceRegistry registry, IDownloadGrants grants, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers["X-Robots-Tag"] = "noindex";

                var credentials = new AccessCredentials(Header(http, AccessWireNames.LinkKeyHeader), Header(http, AccessWireNames.GrantHeader));
                var gated = await gate.EvaluateAsync(code, credentials, AccessPurpose.Download, ct);
                if (gated.Outcome != AccessGateOutcome.Allowed) return ApiResults.Problem(ContentAccessGate.ToError(gated));
                if (gated.Kind is not (LinkTargetKind.Media or LinkTargetKind.File) || gated.ResourceId is not { } resourceId) return ApiResults.NotFound();
                var resource = await registry.GetAsync(resourceId, ct);
                if (resource is null) return ApiResults.NotFound();

                var state = ResourceStateCalculator.Compute(gated.LinkState, resource.Processing, resource.BurnClaimedAt, null);
                switch (state)
                {
                    case ResourceState.Ready: break;
                    case ResourceState.Processing: return ApiResults.Problem(Error.Of(ErrorCodes.Processing, "This content is not ready."));
                    case ResourceState.Rejected: return ApiResults.Problem(Error.Of(ErrorCodes.ContentRejected, "This content is not available."));
                    case ResourceState.Burned: return ApiResults.Problem(Error.Of(ErrorCodes.Burned, "This content is gone."));
                    default: return ApiResults.Problem(Error.Of(ErrorCodes.Expired, "This content has expired."));
                }
                var ticket = await grants.MintAsync(new MintTicketRequest(resourceId, DeliveryPurpose.Download, null), ct);
                return ticket.IsSuccess ? Results.Ok(new DownloadTicketDto(ticket.Value!.Url, ticket.Value.ExpiresAt)) : ApiResults.Problem(ticket.Error!);
            })
            .RequireRateLimiting("content-read");
    }

    /// <summary>File descriptor (ARC-019 section 11.1): the card data and never a URL of any kind. <c>burnAfterDownload</c> tells the page to show the
    /// once-only notice; the ticket exists only behind the button's POST. The hash is not exposed (addendum OPEN).</summary>
    private static async Task<IResult> FileDescriptorAsync(
        ResourceDescriptor resource, DateTime expiresAt, bool hasPassword, IStoredObjectSummaries objects, ContentOptions content, CancellationToken ct)
    {
        var summary = (await objects.GetAsync([resource.ResourceId], ct)).GetValueOrDefault(resource.ResourceId);
        if (summary is null || summary.Scan != ScanVerdict.Clean)
            return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));
        var mime = summary.DetectedMime ?? "application/octet-stream";
        return Results.Ok(new ContentDescriptorDto("file", "ready", resource.Title, summary.SizeBytes, mime, summary.DisplayName, expiresAt,
            resource.BurnAfterDownload, hasPassword, null, null, null, null, null,
            new FileBlockDto(mime, summary.SizeBytes, null, content.MaxResponseBytes)));
    }

    /// <summary>Media descriptor (ARC-011 section 9, S1): the original behind an Inline ticket, the detected type, no derivatives.
    /// <c>mime</c> and <c>filename</c> come from the stored object; nothing here ever contains a download URL.</summary>
    private static async Task<IResult> MediaDescriptorAsync(
        ResourceDescriptor resource, DateTime expiresAt, bool hasPassword, IStoredObjectSummaries objects, ContentOptions content,
        IDownloadGrants grants, CancellationToken ct)
    {
        var summary = (await objects.GetAsync([resource.ResourceId], ct)).GetValueOrDefault(resource.ResourceId);
        // Fail closed: a ready media resource whose object row is missing, or not scanned clean, is not served.
        if (summary is null || summary.Scan != ScanVerdict.Clean || summary.DetectedMime is not { } mime)
            return ApiResults.Problem(Error.Of(ErrorCodes.Gone, "This content is no longer available."));

        string? inlineUrl = null;
        if (content.InlineMimeAllowlist.Contains(mime, StringComparer.OrdinalIgnoreCase) && !resource.BurnAfterDownload)
        {
            var ticket = await grants.MintAsync(new MintTicketRequest(resource.ResourceId, DeliveryPurpose.Inline, null), ct);
            if (!ticket.IsSuccess) return ApiResults.Problem(ticket.Error!);
            inlineUrl = ticket.Value!.Url;
        }
        return Results.Ok(new ContentDescriptorDto("media", "ready", resource.Title, summary.SizeBytes, mime, summary.DisplayName, expiresAt,
            false, hasPassword, inlineUrl, null, null, null, ResourcesEndpoints.MediaBlock(mime), null));
    }

    private static string? Header(HttpContext http, string name)
    {
        var value = http.Request.Headers[name].ToString();
        return value.Length == 0 ? null : value;
    }
}
