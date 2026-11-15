using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;
using LinkOptions = Zaagl.Modules.Links.Application.LinkOptions;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi CompareCreateRequest (docs/phase2/compare-slider.md).</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record CompareCreateRequest(
    Guid BeforeResourceId,
    Guid AfterResourceId,
    string? BeforeLabel = null,
    string? AfterLabel = null,
    string? Title = null,
    DateTime? ExpiresAt = null,
    string? Password = null,
    string? CustomSlug = null,
    bool Public = false);

/// <summary>openapi CompareUpdateRequest: a label sent as "" clears it; absent leaves it unchanged.</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record CompareUpdateRequest(string? BeforeLabel = null, string? AfterLabel = null, bool Swap = false);

/// <summary>openapi CommentPostRequest (docs/phase2/palette-and-comments.md). <c>x</c> and <c>y</c> are required JSON numbers (a missing
/// coordinate is a 400, never a pin at 0).</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record CommentPostRequest(double? X, double? Y, string? Body, string? Side = null, string? AuthorName = null);

/// <summary>openapi CommentSettingsRequest.</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record CommentSettingsRequest(bool Enabled, bool AllowAnonymous = false);

/// <summary>openapi ApprovalCreateRequest (docs/phase2/approval-gate.md).</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record ApprovalCreateRequest(
    Guid DraftResourceId,
    string? WatermarkText = null,
    bool ReleaseOnApproval = true,
    string? Title = null,
    DateTime? ExpiresAt = null,
    string? Password = null,
    string? CustomSlug = null,
    bool Public = false);

/// <summary>openapi ApproveRequest.</summary>
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record ApproveRequest(string? Name = null, string? Email = null, string? Note = null);

/// <summary>
/// Phase 2 E2.2 artist and designer tools on <c>api.zaa.gl</c> (docs/phase2). Owner routes are owner only (404 otherwise, PD-40),
/// <c>private, no-store</c>; public routes run <see cref="IAccessGate"/> on every call.
/// </summary>
public static class CollaborationEndpoints
{
    /// <summary>Spec §7.12 action name for anonymous comments (shared with the website's CAPTCHA widget).</summary>
    public const string GuestCommentAction = "guest-comment";

    /// <summary>Spec §7.12 action name for an approval by a visitor without an account.</summary>
    public const string GuestApproveAction = "guest-approve";

    public static void MapCollaboration(this IEndpointRouteBuilder app)
    {
        MapCompare(app);
        MapComments(app);
        MapApprovals(app);
    }

    private static void MapApprovals(IEndpointRouteBuilder app)
    {
        var approvals = app.MapGroup("/api/approvals").WithTags("Approval gate");

        approvals.MapPost("", async (ApprovalCreateRequest request, ClaimsPrincipal user, HttpContext http, ApprovalService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var created = await service.CreateAsync(user.GetUserId(), SnippetsEndpoints.CreatedViaFor(user), new CreateApprovalCommand(
                    request.DraftResourceId, request.WatermarkText, request.ReleaseOnApproval, request.Title, request.ExpiresAt,
                    request.Password, request.CustomSlug, request.Public), ct);
                return ApiResults.From(created, dto => Results.Created($"/api/approvals/{dto.ResourceId}", dto));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithSummary("Create a watermarked approval link for one of your ready images (Starter).")
            .Produces<ToolLinkCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        approvals.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, ApprovalService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .Produces<OwnerApprovalDto>().ProducesProblem(StatusCodes.Status404NotFound);

        approvals.MapPost("/{id:guid}/release", async (Guid id, ClaimsPrincipal user, HttpContext http, ApprovalService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.ReleaseAsync(id, user.GetUserId(), ct));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Release the original of an approved (or paid) draft when release-on-approval is off.")
            .Produces<ApprovalInfoDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        approvals.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, ApprovalService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteAsync(id, user.GetUserId(), ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var open = app.MapGroup("/api/public/approvals").WithTags("Approval gate (public)").AllowAnonymous();

        open.MapGetAndHead("/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links, IResourceRegistry registry,
                ApprovalService service, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Approval, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
                var resource = await registry.GetAsync(gated.ResourceId, ct);
                if (link is null || resource is null) return ApiResults.NotFound();
                return ApiResults.From(await service.DescribeAsync(gated.ResourceId, link, resource.Title, ct));
            })
            .RequireRateLimiting("content-read")
            .WithSummary("Approval descriptor: the watermarked preview until release, then the original (never a download ticket here).")
            .Produces<ApprovalDescriptorDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);

        open.MapPost("/{code}/approve", async (string code, ApproveRequest request, ClaimsPrincipal user, HttpContext http, IAccessGate gate,
                ApprovalService service, IOptions<GuestOptions> guests, GuestRateLimiter limiter, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Approval, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                Guid? userId = null;
                if (user.Identity?.IsAuthenticated == true)
                {
                    userId = user.GetUserId();
                }
                else
                {
                    var captcha = await CaptchaGate.CheckAsync(http, GuestApproveAction, ct);
                    if (captcha is not null) return captcha;
                    if (!limiter.TryPerMinute("approve:" + IpPartition.Key(http.Connection.RemoteIpAddress), guests.Value.PerMinute))
                        return ApiResults.Problem(Error.TooManyRequests("Too many requests. Try again in a minute."));
                }
                return ApiResults.From(await service.ApproveAsync(gated.ResourceId, userId, new ApproveCommand(request.Name, request.Email, request.Note), ct));
            })
            .RejectFailedCredentials()
            .RequireRateLimiting("comment-write")
            .WithRequestBodyLimit(16 * 1024)
            .WithSummary("Approve the draft (recorded with time and optional name/email/note). Anonymous visitors need the Captcha-Token header.")
            .Produces<ApprovalInfoDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);

        open.MapPost("/{code}/download", async (string code, HttpContext http, IAccessGate gate, ApprovalService service, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Approval, ct, AccessPurpose.Download);
                if (gated.Refusal is not null) return gated.Refusal;
                var ticket = await service.DownloadAsync(gated.ResourceId, ct);
                return ticket.IsSuccess ? Results.Ok(new DownloadTicketDto(ticket.Value!.Url, ticket.Value.ExpiresAt)) : ApiResults.Problem(ticket.Error!);
            })
            .RequireRateLimiting("content-read")
            .WithSummary("After release only: a download ticket for the clean original (valid 120 s if unused).")
            .Produces<DownloadTicketDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapComments(IEndpointRouteBuilder app)
    {
        // ---- Link holders: the gate of the image's, comparison's or approval's own link (state, key, password grant) ----
        var open = app.MapGroup("/api/public/comments").WithTags("Pinned comments (public)").AllowAnonymous();

        open.MapGetAndHead("/{code}", async (string code, ClaimsPrincipal user, HttpContext http, IAccessGate gate, IResourceRegistry registry,
                CommentService comments, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var target = await CommentTargetAsync(http, gate, registry, code, ct);
                if (target.Refusal is not null) return target.Refusal;
                var viewer = user.Identity?.IsAuthenticated == true ? user.GetUserId() : (Guid?)null;
                return ApiResults.From(await comments.ListAsync(target.ResourceId, viewer, ct));
            })
            .RejectFailedCredentials()
            .RequireRateLimiting("content-read")
            .WithSummary("The pinned comments of an image, comparison or approval draft, for anyone holding its link.")
            .Produces<CommentThreadDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);

        open.MapPost("/{code}", async (string code, CommentPostRequest request, ClaimsPrincipal user, HttpContext http, IAccessGate gate,
                IResourceRegistry registry, CommentService comments, IOptions<GuestOptions> guests, GuestRateLimiter limiter, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                if (request.X is not { } x || request.Y is not { } y)
                    return ApiResults.Problem(Error.Validation("x and y are required numbers between 0 and 1."));
                var target = await CommentTargetAsync(http, gate, registry, code, ct);
                if (target.Refusal is not null) return target.Refusal;
                var anonymous = user.Identity?.IsAuthenticated != true;
                // Settings first (comments off 403, anonymous posting off 401): a request that will be refused anyway never spends a
                // CAPTCHA verification or a guest-brake slot. PostAsync re-checks both under the row lock.
                var allowed = await comments.CheckCanPostAsync(target.ResourceId, anonymous, ct);
                if (!allowed.IsSuccess) return ApiResults.Problem(allowed.Error!);
                Guid? author = null;
                if (!anonymous)
                {
                    author = user.GetUserId();
                }
                else
                {
                    // Anonymous visitors: the spec §7.12 CAPTCHA and the in-memory brake, like every other guest write.
                    var captcha = await CaptchaGate.CheckAsync(http, GuestCommentAction, ct);
                    if (captcha is not null) return captcha;
                    var partition = IpPartition.Key(http.Connection.RemoteIpAddress);
                    if (!limiter.TryPerMinute("cmt:" + partition, guests.Value.PerMinute))
                        return ApiResults.Problem(Error.TooManyRequests("Too many comments. Try again in a minute."));
                }
                var posted = await comments.PostAsync(target.ResourceId, target.OwnerId, target.Kind, new CommentAuthor(author),
                    new PostCommentCommand(x, y, request.Side, request.Body, request.AuthorName), ct);
                return ApiResults.From(posted, c => Results.Created($"/api/public/comments/{code}/{c.Id}", c));
            })
            .RejectFailedCredentials()
            .RequireRateLimiting("comment-write")
            .WithRequestBodyLimit(16 * 1024)
            .WithSummary("Pin a comment at a normalised point (x, y in 0..1). Anonymous visitors need the Captcha-Token header.")
            .Produces<CommentDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status429TooManyRequests);

        open.MapDelete("/{code}/{commentId:long}", async (string code, long commentId, ClaimsPrincipal user, HttpContext http, IAccessGate gate,
                IResourceRegistry registry, CommentService comments, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                if (user.Identity?.IsAuthenticated != true) return ApiResults.Problem(Error.Unauthorized("Sign in to delete your comment."));
                var target = await CommentTargetAsync(http, gate, registry, code, ct);
                if (target.Refusal is not null) return target.Refusal;
                var deleted = await comments.AuthorDeleteAsync(target.ResourceId, user.GetUserId(), commentId, ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RejectFailedCredentials()
            .RequireRateLimiting("comment-write")
            .WithSummary("A signed-in author deletes their own comment.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        // ---- Owner moderation ----
        var owner = app.MapGroup("/api/resources/{id:guid}/comments").WithTags("Pinned comments");

        owner.MapPut("/settings", async (Guid id, CommentSettingsRequest request, ClaimsPrincipal user, HttpContext http, CommentService comments, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await comments.SetSettingsAsync(id, user.GetUserId(), request.Enabled, request.AllowAnonymous, ct));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Turn pinned comments on or off for one of your images, comparisons or approval drafts.")
            .Produces<CommentSettingsDto>().ProducesProblem(StatusCodes.Status402PaymentRequired).ProducesProblem(StatusCodes.Status404NotFound);

        owner.MapGet("", async (Guid id, ClaimsPrincipal user, HttpContext http, CommentService comments, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await comments.OwnerListAsync(id, user.GetUserId(), ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .Produces<CommentThreadDto>().ProducesProblem(StatusCodes.Status404NotFound);

        owner.MapPost("/{commentId:long}/resolve", async (Guid id, long commentId, ClaimsPrincipal user, HttpContext http, CommentService comments, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await comments.SetStatusAsync(id, user.GetUserId(), commentId, resolved: true, ct));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .Produces<CommentDto>().ProducesProblem(StatusCodes.Status404NotFound);

        owner.MapPost("/{commentId:long}/reopen", async (Guid id, long commentId, ClaimsPrincipal user, HttpContext http, CommentService comments, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await comments.SetStatusAsync(id, user.GetUserId(), commentId, resolved: false, ct));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .Produces<CommentDto>().ProducesProblem(StatusCodes.Status404NotFound);

        owner.MapDelete("/{commentId:long}", async (Guid id, long commentId, ClaimsPrincipal user, HttpContext http, CommentService comments, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await comments.OwnerDeleteAsync(id, user.GetUserId(), commentId, ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private readonly record struct CommentTarget(IResult? Refusal, Guid ResourceId, Guid OwnerId, ResourceKind Kind);

    /// <summary>The gate for purpose View on the code, then: a resource kind that can carry comments, owned by an account (guest content
    /// has no owner to moderate it), and ready. Anything else answers like an unknown code or a closed one.</summary>
    private static async Task<CommentTarget> CommentTargetAsync(HttpContext http, IAccessGate gate, IResourceRegistry registry, string code, CancellationToken ct)
    {
        var credentials = new AccessCredentials(DeveloperToolsEndpoints.Header(http, AccessWireNames.LinkKeyHeader),
            DeveloperToolsEndpoints.Header(http, AccessWireNames.GrantHeader));
        var gated = await gate.EvaluateAsync(code, credentials, AccessPurpose.View, ct);
        if (gated.Outcome != AccessGateOutcome.Allowed)
            return new(ApiResults.Problem(ContentAccessGate.ToError(gated)), default, default, default);
        if (gated.ResourceId is not { } resourceId || await registry.GetAsync(resourceId, ct) is not { } resource
            || !CommentService.Targets.Contains(resource.Kind) || resource.Owner.UserId is not { } ownerId)
            return new(ApiResults.NotFound(), default, default, default);
        var state = ResourceStateCalculator.Compute(gated.LinkState, resource.Processing, resource.BurnClaimedAt, null);
        if (state != ResourceState.Ready)
            return new(ApiResults.Problem(state == ResourceState.Processing
                ? Error.Of(ErrorCodes.Processing, "This content is not ready.")
                : Error.Of(ErrorCodes.Expired, "This content is not available.")), default, default, default);
        return new(null, resourceId, ownerId, resource.Kind);
    }

    private static void MapCompare(IEndpointRouteBuilder app)
    {
        var compare = app.MapGroup("/api/compare").WithTags("Before/after slider");

        compare.MapPost("", async (CompareCreateRequest request, ClaimsPrincipal user, HttpContext http, CompareService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var created = await service.CreateAsync(user.GetUserId(), SnippetsEndpoints.CreatedViaFor(user), new CreateComparisonCommand(
                    request.BeforeResourceId, request.AfterResourceId, request.BeforeLabel, request.AfterLabel, request.Title,
                    request.ExpiresAt, request.Password, request.CustomSlug, request.Public), ct);
                return ApiResults.From(created, dto => Results.Created($"/api/compare/{dto.ResourceId}", dto));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithSummary("Create a before/after comparison link from two of the caller's ready images.")
            .Produces<ToolLinkCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        compare.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, CompareService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .Produces<OwnerComparisonDto>().ProducesProblem(StatusCodes.Status404NotFound);

        compare.MapPatch("/{id:guid}", async (Guid id, CompareUpdateRequest request, ClaimsPrincipal user, HttpContext http, CompareService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var updated = await service.UpdateLabelsAsync(id, user.GetUserId(), request.BeforeLabel, request.BeforeLabel is not null,
                    request.AfterLabel, request.AfterLabel is not null, request.Swap, ct);
                if (!updated.IsSuccess) return ApiResults.Problem(updated.Error!);
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Change the labels or swap the sides. Title, expiry, public and password: PATCH /api/resources/{id}.")
            .Produces<OwnerComparisonDto>().ProducesProblem(StatusCodes.Status404NotFound);

        compare.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, CompareService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteAsync(id, user.GetUserId(), ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Delete the comparison link (410). The images are not touched.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGetAndHead("/api/public/compare/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links,
                IResourceRegistry registry, CompareService service, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Compare, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
                var resource = await registry.GetAsync(gated.ResourceId, ct);
                if (link is null || resource is null) return ApiResults.NotFound();
                return ApiResults.From(await service.DescribeAsync(gated.ResourceId, link, resource.Title, ct));
            })
            .WithTags("Before/after slider (public)").AllowAnonymous()
            .RequireRateLimiting("content-read")
            .WithSummary("Comparison descriptor for a link holder: both sides with inline tickets (never a download ticket).")
            .Produces<CompareDescriptorDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
    }
}
