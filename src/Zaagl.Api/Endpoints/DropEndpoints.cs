using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;
using LinkOptions = Zaagl.Modules.Links.Application.LinkOptions;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi DropCreateRequest (docs/phase2/drop-portals.md).</summary>
public sealed record DropCreateRequest(
    string? Title = null,
    string? Instructions = null,
    int? MaxFiles = null,
    long? MaxBytes = null,
    long? MaxFileBytes = null,
    string[]? AllowedTypes = null,
    bool RequireName = false,
    bool RequireEmail = false,
    bool NotifyOwner = true,
    DateTime? ExpiresAt = null,
    string? Password = null,
    string? CustomSlug = null,
    bool Public = false);

/// <summary>openapi DropUpdateRequest: a field left out is unchanged; <c>instructions: ""</c> clears them.</summary>
public sealed record DropUpdateRequest(
    string? Instructions = null,
    int? MaxFiles = null,
    long? MaxBytes = null,
    long? MaxFileBytes = null,
    string[]? AllowedTypes = null,
    bool? RequireName = null,
    bool? RequireEmail = null,
    bool? NotifyOwner = null);

/// <summary>openapi DropCompleteRequest: the uploader's optional identification (required fields per the portal).</summary>
public sealed record DropCompleteRequest(string? Name = null, string? Email = null, string? Note = null);

/// <summary>Body of the portal's tus create (tus clients read <c>Location</c> and <c>X-Zaagl-Drop-Token</c>; others may read this).</summary>
public sealed record DropUploadCreatedDto(Guid UploadId, string UploadUrl, string Token, DateTime ExpiresAt);

/// <summary>
/// Phase 2 E2.3 "Reverse Pigeon" drop-off portals (docs/phase2/drop-portals.md). Owner routes on <c>api.zaa.gl</c> are owner only (404
/// otherwise, PD-40), <c>private, no-store</c>. The uploader routes are anonymous: every call runs the content gate (state, link key,
/// password grant) for the portal code; the tus create additionally runs the guest panel (feature <c>guest-drop</c>: pause and CAPTCHA),
/// the in-memory guest brakes and per-network counters, and each later tus call needs the per-session <c>X-Zaagl-Drop-Token</c> returned by
/// the create. Files land in the owner's library and quota, are scanned before the owner can download them, and an uploader never sees
/// a link to (or any detail of) any upload, not even their own.
/// </summary>
public static class DropEndpoints
{
    public const string TokenHeader = "X-Zaagl-Drop-Token";

    public static void MapDrops(this IEndpointRouteBuilder app)
    {
        MapOwner(app);
        MapUploader(app);
    }

    private static void MapOwner(IEndpointRouteBuilder app)
    {
        var drops = app.MapGroup("/api/drops").WithTags("Drop-off portals");

        drops.MapPost("", async (DropCreateRequest request, ClaimsPrincipal user, HttpContext http, DropPortalService service,
                IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var created = await service.CreateAsync(user.GetUserId(), SnippetsEndpoints.CreatedViaFor(user), new CreateDropPortalCommand(
                    request.Title, request.Instructions, request.MaxFiles, request.MaxBytes, request.MaxFileBytes, request.AllowedTypes,
                    request.RequireName, request.RequireEmail, request.NotifyOwner, request.ExpiresAt, request.Password, request.CustomSlug,
                    request.Public), links.Value.BaseUrl, ct);
                return ApiResults.From(created, dto => Results.Created($"/api/drops/{dto.ResourceId}", dto));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithSummary("Create a drop-off portal (zaa.gl/drop/<code>) that people without an account can upload files into.")
            .Produces<ToolLinkCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict);

        drops.MapGet("", async (ClaimsPrincipal user, HttpContext http, DropPortalService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok(await service.ListAsync(user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("The caller's portals, newest first (at most 100), with usage counts.")
            .Produces<IReadOnlyList<OwnerDropPortalDto>>();

        drops.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, DropPortalService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .Produces<OwnerDropPortalDto>().ProducesProblem(StatusCodes.Status404NotFound);

        drops.MapPatch("/{id:guid}", async (Guid id, DropUpdateRequest request, ClaimsPrincipal user, HttpContext http, DropPortalService service,
                IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var updated = await service.UpdateAsync(id, user.GetUserId(), new UpdateDropPortalCommand(request.Instructions, request.MaxFiles,
                    request.MaxBytes, request.MaxFileBytes, request.AllowedTypes, request.RequireName, request.RequireEmail, request.NotifyOwner), ct);
                if (!updated.IsSuccess) return ApiResults.Problem(updated.Error!);
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Change the portal's settings. Title, expiry, public and password: PATCH /api/resources/{id}.")
            .Produces<OwnerDropPortalDto>().ProducesProblem(StatusCodes.Status404NotFound);

        drops.MapPost("/{id:guid}/close", (Guid id, ClaimsPrincipal user, HttpContext http, DropPortalService service, IOptions<LinkOptions> links, CancellationToken ct) =>
                SetClosedAsync(id, true, user, http, service, links, ct))
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Stop accepting new uploads (uploads already started may finish).")
            .Produces<OwnerDropPortalDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        drops.MapPost("/{id:guid}/reopen", (Guid id, ClaimsPrincipal user, HttpContext http, DropPortalService service, IOptions<LinkOptions> links, CancellationToken ct) =>
                SetClosedAsync(id, false, user, http, service, links, ct))
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Accept uploads again (counts toward the plan's open-portal cap).")
            .Produces<OwnerDropPortalDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        drops.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, DropPortalService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteAsync(id, user.GetUserId(), ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Delete the portal link (410). Files already received stay in your library.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        drops.MapGet("/{id:guid}/files", async (Guid id, ClaimsPrincipal user, HttpContext http, DropPortalService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.FilesAsync(id, user.GetUserId(), ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("Files received by the portal, newest first, with the uploader's given name/email/note and the scan state.")
            .Produces<IReadOnlyList<DropFileDto>>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> SetClosedAsync(Guid id, bool closed, ClaimsPrincipal user, HttpContext http, DropPortalService service,
        IOptions<LinkOptions> links, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        var changed = await service.SetClosedAsync(id, user.GetUserId(), closed, ct);
        if (!changed.IsSuccess) return ApiResults.Problem(changed.Error!);
        return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
    }

    private static void MapUploader(IEndpointRouteBuilder app)
    {
        var open = app.MapGroup("/api/public/drops").WithTags("Drop-off portals (uploader)").AllowAnonymous().RejectFailedCredentials();

        open.MapGet("/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links, IResourceRegistry registry,
                DropPortalService service, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Drop, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
                var resource = await registry.GetAsync(gated.ResourceId, ct);
                if (link is null || resource is null) return ApiResults.NotFound();
                return ApiResults.From(await service.DescribeAsync(gated.ResourceId, link, resource.Title, ct));
            })
            .RequireRateLimiting("content-read")
            .WithSummary("Portal descriptor for an uploader: title, instructions, accepted types and sizes. Nothing about other uploads.")
            .Produces<DropDescriptorDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);

        // tus create: gate, guest panel (pause + CAPTCHA per upload session), brakes, per-network counters, then the portal caps
        // (Files, under the portal's advisory lock) and the owner's quota reservation.
        open.MapPost("/{code}/uploads", async (string code, HttpContext http, IAccessGate gate, DropPortalService portals, UploadService uploads,
                DiskAdmission admission, CancellationToken ct) =>
            {
                FilesEndpoints.Tus(http);
                DeveloperToolsEndpoints.NoStore(http);
                if (FilesEndpoints.TusVersionMismatch(http)) return FilesEndpoints.PreconditionFailed();
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Drop, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var portal = await portals.AdmitAsync(gated.ResourceId, forNewUpload: true, ct);
                if (!portal.IsSuccess) return ApiResults.Problem(portal.Error!);

                if (!long.TryParse(http.Request.Headers["Upload-Length"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                    return FilesEndpoints.Problem(http, Error.Of(ErrorCodes.UploadLengthRequired, "Upload-Length is required."), admission);
                if (http.Request.Headers.ContainsKey("Upload-Defer-Length"))
                    return FilesEndpoints.Problem(http, Error.Of(ErrorCodes.UploadLengthRequired, "A deferred length is not supported."), admission);

                var (admit, refund, refusal) = await AdmitUploaderAsync(http, ct);
                if (refusal is not null) return refusal;

                var token = RandomNumberGenerator.GetBytes(32);
                var tokenText = Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var actor = Actor(portal.Value!, Hash(tokenText), admit, refund);
                var created = await uploads.CreateAsync(actor, length, false,
                    http.Request.Headers["Upload-Metadata"].ToString() is { Length: > 0 } m ? m : null, null, ct);
                if (!created.IsSuccess) return FilesEndpoints.Problem(http, created.Error!, admission);

                var url = $"/api/public/drops/{Uri.EscapeDataString(code)}/uploads/{created.Value!.Id}";
                http.Response.Headers.Location = url;
                http.Response.Headers[TokenHeader] = tokenText;
                http.Response.Headers["Upload-Expires"] = FilesEndpoints.HttpDate(created.Value.ExpiresAt);
                return Results.Json(new DropUploadCreatedDto(created.Value.Id, url, tokenText, created.Value.ExpiresAt), statusCode: StatusCodes.Status201Created);
            })
            .RequireRateLimiting("content-write")
            .WithSummary("tus creation for an uploader (CAPTCHA action guest-drop when the admin panel requires it). Returns X-Zaagl-Drop-Token.")
            .Produces<DropUploadCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        open.MapMethods("/{code}/uploads/{id:guid}", ["HEAD"], async (string code, Guid id, HttpContext http, IAccessGate gate,
                DropPortalService portals, UploadService uploads, DiskAdmission admission, CancellationToken ct) =>
            {
                FilesEndpoints.Tus(http);
                if (FilesEndpoints.TusVersionMismatch(http)) return FilesEndpoints.PreconditionFailed();
                var (actor, refusal) = await SessionActorAsync(http, gate, portals, code, ct);
                if (refusal is not null) return refusal;
                var info = await uploads.GetOffsetAsync(actor!, id, ct);
                if (!info.IsSuccess) return FilesEndpoints.Problem(http, info.Error!, admission);
                http.Response.Headers["Upload-Offset"] = info.Value!.Offset.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["Upload-Length"] = info.Value.Length.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["Upload-Expires"] = FilesEndpoints.HttpDate(info.Value.ExpiresAt);
                http.Response.Headers.CacheControl = "no-store";
                return Results.StatusCode(StatusCodes.Status200OK);
            })
            .WithSummary("tus offset of the uploader's own session (X-Zaagl-Drop-Token).");

        open.MapPatch("/{code}/uploads/{id:guid}", async (string code, Guid id, HttpContext http, IAccessGate gate,
                DropPortalService portals, UploadService uploads, DiskAdmission admission, CancellationToken ct) =>
            {
                FilesEndpoints.Tus(http);
                if (FilesEndpoints.TusVersionMismatch(http)) return FilesEndpoints.PreconditionFailed();
                FilesEndpoints.RaiseBodyLimits(http);
                var request = http.Request;
                if (request.ContentType is null || !request.ContentType.StartsWith("application/offset+octet-stream", StringComparison.OrdinalIgnoreCase))
                    return FilesEndpoints.Problem(http, Error.Of(ErrorCodes.UnsupportedMediaType, "Send the chunk as Content-Type: application/offset+octet-stream."), admission);
                if (!long.TryParse(request.Headers["Upload-Offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    return FilesEndpoints.Problem(http, Error.Validation("Upload-Offset is required."), admission);
                var (actor, refusal) = await SessionActorAsync(http, gate, portals, code, ct);
                if (refusal is not null) return refusal;
                try
                {
                    var result = await uploads.AppendAsync(actor!, id, offset, request.ContentLength, request.Body, ct);
                    if (!result.IsSuccess) return FilesEndpoints.Problem(http, result.Error!, admission);
                    http.Response.Headers["Upload-Offset"] = result.Value!.Offset.ToString(CultureInfo.InvariantCulture);
                    http.Response.Headers["Upload-Expires"] = FilesEndpoints.HttpDate(result.Value.ExpiresAt);
                    return Results.StatusCode(StatusCodes.Status204NoContent);
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return FilesEndpoints.Problem(http, Error.Of(ErrorCodes.ChunkTooLarge, $"A chunk is at most {UploadProtocol.MaxChunkBytes} bytes."), admission);
                }
            })
            .WithRequestBodyLimit(UploadProtocol.MaxChunkBytes)
            .WithSummary("tus append to the uploader's own session (X-Zaagl-Drop-Token).");

        open.MapDelete("/{code}/uploads/{id:guid}", async (string code, Guid id, HttpContext http, IAccessGate gate,
                DropPortalService portals, UploadService uploads, DiskAdmission admission, CancellationToken ct) =>
            {
                FilesEndpoints.Tus(http);
                var (actor, refusal) = await SessionActorAsync(http, gate, portals, code, ct);
                if (refusal is not null) return refusal;
                var aborted = await uploads.AbortAsync(actor!, id, ct);
                return aborted.IsSuccess ? Results.StatusCode(StatusCodes.Status204NoContent) : FilesEndpoints.Problem(http, aborted.Error!, admission);
            })
            .WithSummary("tus termination of the uploader's own session: bytes deleted, the owner's reservation and the portal slot released.");

        open.MapPost("/{code}/uploads/{id:guid}/complete", async (string code, Guid id, DropCompleteRequest? body, HttpContext http, IAccessGate gate,
                DropPortalService portals, UploadService uploads, DiskAdmission admission, IOwnerNotices notices, ILogger<DropPortalService> logger,
                CancellationToken ct) =>
            {
                FilesEndpoints.Tus(http);
                DeveloperToolsEndpoints.NoStore(http);
                var idempotencyKey = SnippetsEndpoints.IdempotencyKeyFrom(http.Request);
                if (idempotencyKey is null)
                    return FilesEndpoints.Problem(http, Error.Validation("The Idempotency-Key header is required (1-128 printable ASCII characters)."), admission);
                var (actor, refusal) = await SessionActorAsync(http, gate, portals, code, ct);
                if (refusal is not null) return refusal;
                var portal = await portals.AdmitAsync(actor!.Drop!.PortalId, forNewUpload: false, ct);
                if (!portal.IsSuccess) return ApiResults.Problem(portal.Error!);
                var uploader = DropPortalService.Uploader(portal.Value!, body?.Name, body?.Email, body?.Note);
                if (!uploader.IsSuccess) return ApiResults.Problem(uploader.Error!);

                var completed = await uploads.CompleteAsync(actor, id, new CompleteUploadRequest(null, null, false, null, null, false), idempotencyKey, ct);
                if (!completed.IsSuccess) return FilesEndpoints.Problem(http, completed.Error!, admission);
                var notice = await portals.RecordFileAsync(portal.Value!, completed.Value!.ResourceId, id, uploader.Value!, ct);
                if (notice is not null)
                {
                    try
                    {
                        await notices.QueueDropReceivedAsync(notice.OwnerId, notice.PortalId, notice.Title, notice.NewFiles, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not queue the drop notice of portal {PortalId}", notice.PortalId);
                    }
                }
                var summary = await DescribeReceiptAsync(http, completed.Value.ResourceId, ct);
                return Results.Json(new DropReceiptDto(id, "received", summary.Name, summary.Size), statusCode: StatusCodes.Status202Accepted);
            })
            .RequireRateLimiting("content-write")
            .WithSummary("Finish the upload: the file goes to the owner (scanned before they can download it). Returns a receipt, never a link.")
            .Produces<DropReceiptDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Short host: <c>zaa.gl/drop/&lt;code&gt;</c> answers like <c>zaa.gl/&lt;code&gt;</c> for a drop portal (302 to
    /// <c>app.zaa.gl/drop/&lt;code&gt;</c>, the browser keeps <c>#k=</c>); any other code or kind is the branded 404.</summary>
    public static void MapDropShortRoute(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/drop/{slug}", ["GET", "HEAD"], async (string slug, HttpContext http,
                IRedirectRateLimiter limiter, IConfiguration config, CancellationToken ct) =>
            {
                if (!SlugRules.IsValidFormat(slug)) return StatusPages.NotFound; // the same shape rule as /{slug}, before any I/O
                http.Response.Headers.CacheControl = ResolutionEndpoints.RedirectCacheControl;
                http.Response.Headers["X-Robots-Tag"] = "noindex";
                var address = http.Connection.RemoteIpAddress;
                var decision = limiter.TryAcquire(address);
                if (!decision.Allowed)
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    return Results.Text("Too many requests.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status429TooManyRequests);
                }
                var links = http.RequestServices.GetRequiredService<ILinkResolver>();
                var link = await links.GetSnapshotAsync(SlugRules.Key(slug), ct);
                if (link is null || link.TargetKind != LinkTargetKind.Drop)
                {
                    limiter.RecordMiss(address);
                    return StatusPages.NotFound;
                }
                var web = (config["Frontend:BaseUrl"] ?? "").TrimEnd('/');
                return Results.Redirect($"{web}/{ViewerPathSegments.Drop}/{Uri.EscapeDataString(link.Slug)}");
            })
            .WithTags("Resolution").ExcludeFromDescription();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static UploadActor Actor(DropAdmission p, byte[] tokenHash, Func<CancellationToken, Task<bool>>? admit = null, Func<Task>? refund = null) =>
        new(p.OwnerId, null, CreatedVia.Web, admit, refund,
            new DropUpload(p.PortalId, tokenHash, p.MaxFiles, p.MaxBytes, p.MaxPerHour, p.MaxFileBytes, p.AllowedTypes));

    /// <summary>HEAD/PATCH/DELETE/complete: the gate for the code (a revoked key or a deleted portal stops a resumed upload), then the
    /// uploader's token. A missing token is the same 404 as someone else's session.</summary>
    private static async Task<(UploadActor? Actor, IResult? Refusal)> SessionActorAsync(HttpContext http, IAccessGate gate, DropPortalService portals,
        string code, CancellationToken ct)
    {
        var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Drop, ct);
        if (gated.Refusal is not null) return (null, gated.Refusal);
        var token = http.Request.Headers[TokenHeader].ToString();
        if (token.Length is < 16 or > 128) return (null, ApiResults.NotFound());
        var portal = await portals.AdmitAsync(gated.ResourceId, forNewUpload: false, ct);
        if (!portal.IsSuccess) return (null, ApiResults.Problem(portal.Error!));
        return (Actor(portal.Value!, Hash(token)), null);
    }

    /// <summary>
    /// The guest gates of an uploader, in the Phase 1 order: kill switch and the admin panel (feature <c>guest-drop</c>: pause, CAPTCHA),
    /// the in-memory brakes, then per-network counters (configuration <c>Drops:PerIpPerHour</c>/<c>Drops:PerIpPerDay</c>, separate from
    /// the PD-14 content counters) that the upload service consumes after its own checks and refunds when the create fails.
    /// </summary>
    private static async Task<(Func<CancellationToken, Task<bool>>? Admit, Func<Task>? Refund, IResult? Refusal)> AdmitUploaderAsync(HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var (settings, refusal) = await GuestGate.AdmitAsync(http, GuestFeatures.Drop, ct);
        if (refusal is not null) return (null, null, refusal);
        var limits = settings!.Limits;
        var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
        var limiter = services.GetRequiredService<GuestRateLimiter>();
        if (!limiter.TryGlobalCeiling(partition, limits.GlobalPerHour))
            return (null, null, ApiResults.Problem(new Error(ErrorCodes.GuestCreationPaused, "Uploads are temporarily paused for this network. Try again shortly.")));
        if (!limiter.TryPerMinute(partition, limits.PerMinute))
            return (null, null, ApiResults.Problem(Error.TooManyRequests("Too many requests. Try again in a minute.")));

        var config = services.GetRequiredService<IConfiguration>();
        var perHour = Math.Clamp(config.GetValue("Drops:PerIpPerHour", 30), 1, 10_000);
        var perDay = Math.Max(perHour, Math.Clamp(config.GetValue("Drops:PerIpPerDay", 100), 1, 100_000));
        var guestKey = services.GetRequiredService<GuestKeyProvider>();
        var linksDb = services.GetRequiredService<LinksDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var counterKey = GuestIdentity.DeriveKey(guestKey.Key, "drop:" + partition);
        DateTime consumedAt = default;
        var admitted = false;
        return (async token =>
            {
                consumedAt = clock.GetUtcNow().UtcDateTime;
                admitted = await GuestContentQuota.TryConsumeAsync(linksDb, counterKey, perHour, perDay, consumedAt, token);
                return admitted;
            },
            async () =>
            {
                if (!admitted) return;
                admitted = false;
                await GuestContentQuota.RefundAsync(linksDb, counterKey, consumedAt);
            }, null);
    }

    private static async Task<(string? Name, long Size)> DescribeReceiptAsync(HttpContext http, Guid resourceId, CancellationToken ct)
    {
        var summaries = http.RequestServices.GetRequiredService<IStoredObjectSummaries>();
        var summary = (await summaries.GetAsync([resourceId], ct)).GetValueOrDefault(resourceId);
        return (summary?.DisplayName, summary?.SizeBytes ?? 0);
    }
}
