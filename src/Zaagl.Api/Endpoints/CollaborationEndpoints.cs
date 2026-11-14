using System.Security.Claims;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Access.Contracts;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using LinkOptions = Zaagl.Modules.Links.Application.LinkOptions;

namespace Zaagl.Api.Endpoints;

/// <summary>openapi CompareCreateRequest (docs/phase2/compare-slider.md).</summary>
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
public sealed record CompareUpdateRequest(string? BeforeLabel = null, string? AfterLabel = null, bool Swap = false);

/// <summary>
/// Phase 2 E2.2 artist and designer tools on <c>api.zaa.gl</c> (docs/phase2). Owner routes are owner only (404 otherwise, PD-40),
/// <c>private, no-store</c>; public routes run <see cref="IAccessGate"/> on every call.
/// </summary>
public static class CollaborationEndpoints
{
    public static void MapCollaboration(this IEndpointRouteBuilder app)
    {
        MapCompare(app);
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

        app.MapGet("/api/public/compare/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links,
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
