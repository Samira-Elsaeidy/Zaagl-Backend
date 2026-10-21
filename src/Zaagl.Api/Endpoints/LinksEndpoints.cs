using System.Security.Claims;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>A link plus its all-time human click count, as shown in the link library.</summary>
public sealed record LinkListItem(LinkDto Link, long Clicks);

public static class LinksEndpoints
{
    private const long MaxCsvBytes = 1_048_576;

    public static void MapLinks(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/api/links").WithTags("Links").RequireAuthorization();

        links.MapGet("", async (
            [AsParameters] ListLinksQuery query, ClaimsPrincipal user,
            LinkService service, IAnalyticsQueries analytics, CancellationToken ct) =>
        {
            var page = await service.ListAsync(user.GetUserId(), query, ct);
            var totals = await analytics.GetClickTotalsAsync(page.Items.Select(l => l.Id).ToList(), ct);
            var items = page.Items.Select(l => new LinkListItem(l, totals.GetValueOrDefault(l.Id))).ToList();
            return Results.Ok(new PagedResult<LinkListItem>(items, page.Total, page.Page, page.PageSize));
        });

        links.MapPost("", async (LinkInput input, ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
                ApiResults.From(await service.CreateAsync(user.GetUserId(), input, ct),
                    dto => Results.Created($"/api/links/{dto.Id}", dto)))
            .RequireRateLimiting("link-write");

        links.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
            await service.GetAsync(user.GetUserId(), id, ct) is { } dto ? Results.Ok(dto) : ApiResults.NotFound("Link not found."));

        links.MapPut("/{id:guid}", async (Guid id, LinkInput input, ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
                ApiResults.From(await service.UpdateAsync(user.GetUserId(), id, input, ct)))
            .RequireRateLimiting("link-write");

        links.MapPatch("/{id:guid}", async (Guid id, LinkFlagsInput flags, ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
            ApiResults.From(await service.SetFlagsAsync(user.GetUserId(), id, flags, ct)));

        links.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
            await service.DeleteAsync(user.GetUserId(), id, ct) ? Results.NoContent() : ApiResults.NotFound("Link not found."));

        links.MapGet("/slug-available", async (string? slug, LinkService service, CancellationToken ct) =>
            Results.Ok(await service.CheckSlugAsync(slug, ct)));

        links.MapPost("/bulk", async (IFormFile file, ClaimsPrincipal user, BulkLinkImporter importer, CancellationToken ct) =>
            {
                if (file.Length is 0 or > MaxCsvBytes)
                    return ApiResults.Problem(Error.Validation("Upload a CSV file of up to 1 MB."));

                await using var stream = file.OpenReadStream();
                return ApiResults.From(await importer.ImportAsync(user.GetUserId(), stream, ct));
            })
            .DisableAntiforgery()
            .RequireRateLimiting("link-write");

        app.MapGet("/api/tags", async (ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
            Results.Ok(await service.ListTagsAsync(user.GetUserId(), ct)))
            .WithTags("Links").RequireAuthorization();

        var folders = app.MapGroup("/api/folders").WithTags("Folders").RequireAuthorization();

        folders.MapGet("", async (ClaimsPrincipal user, FolderService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(user.GetUserId(), ct)));

        folders.MapPost("", async (FolderInput input, ClaimsPrincipal user, FolderService service, CancellationToken ct) =>
            ApiResults.From(await service.CreateAsync(user.GetUserId(), input.Name, ct),
                dto => Results.Created($"/api/folders/{dto.Id}", dto)));

        folders.MapPut("/{id:guid}", async (Guid id, FolderInput input, ClaimsPrincipal user, FolderService service, CancellationToken ct) =>
            ApiResults.From(await service.RenameAsync(user.GetUserId(), id, input.Name, ct)));

        folders.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, FolderService service, CancellationToken ct) =>
            await service.DeleteAsync(user.GetUserId(), id, ct) ? Results.NoContent() : ApiResults.NotFound("Folder not found."));
    }
}
