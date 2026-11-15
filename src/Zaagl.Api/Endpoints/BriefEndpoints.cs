using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using LinkOptions = Zaagl.Modules.Links.Application.LinkOptions;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi BriefCreateRequest (docs/phase2/voice-briefs.md).</summary>
public sealed record BriefCreateRequest(
    Guid SubjectResourceId,
    Guid AudioResourceId,
    int? DurationMs,
    System.Text.Json.JsonElement Timeline = default,
    string? Title = null,
    DateTime? ExpiresAt = null,
    string? Password = null,
    string? CustomSlug = null,
    bool Public = false);

/// <summary>
/// Phase 2 E2.3 voice-over micro-briefs (docs/phase2/voice-briefs.md): one shareable link (<c>app.zaa.gl/vb/&lt;code&gt;</c>) that plays a
/// short recording over an image or PDF with a pointer timeline. Accounts create (owner routes owner only, 404 otherwise,
/// <c>private, no-store</c>); a listener needs no account (the content gate runs on every read; nothing is consumed).
/// </summary>
public static class BriefEndpoints
{
    /// <summary>The largest timeline (20,000 events × 400 bytes, the configuration ceiling) plus JSON overhead.</summary>
    private const long CreateBodyBytes = 9L * 1024 * 1024;

    public static void MapBriefs(this IEndpointRouteBuilder app)
    {
        var briefs = app.MapGroup("/api/briefs").WithTags("Voice-over briefs");

        briefs.MapPost("", async (BriefCreateRequest request, ClaimsPrincipal user, HttpContext http, BriefService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var created = await service.CreateAsync(user.GetUserId(), SnippetsEndpoints.CreatedViaFor(user), new CreateBriefCommand(
                    request.SubjectResourceId, request.AudioResourceId, request.DurationMs, request.Timeline, request.Title, request.ExpiresAt,
                    request.Password, request.CustomSlug, request.Public), ct);
                return ApiResults.From(created, dto => Results.Created($"/api/briefs/{dto.ResourceId}", dto));
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(CreateBodyBytes)
            .WithSummary("Create a voice-over brief: one of your audio recordings over one of your images or PDFs, with an optional pointer timeline.")
            .Produces<ToolLinkCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        briefs.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, BriefService service, IOptions<LinkOptions> links, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .Produces<OwnerBriefDto>().ProducesProblem(StatusCodes.Status404NotFound);

        briefs.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, BriefService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteAsync(id, user.GetUserId(), ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .WithSummary("Delete the brief link (410). The image/PDF and the recording are not touched.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/public/briefs/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links,
                IResourceRegistry registry, BriefService service, CancellationToken ct) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Brief, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
                var resource = await registry.GetAsync(gated.ResourceId, ct);
                if (link is null || resource is null) return ApiResults.NotFound();
                return ApiResults.From(await service.DescribeAsync(gated.ResourceId, link, resource.Title, ct));
            })
            .WithTags("Voice-over briefs (public)").AllowAnonymous()
            .RequireRateLimiting("content-read")
            .WithSummary("Brief descriptor for a listener: subject and audio inline tickets (never a download ticket) and the pointer timeline.")
            .Produces<BriefDescriptorDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
    }
}
