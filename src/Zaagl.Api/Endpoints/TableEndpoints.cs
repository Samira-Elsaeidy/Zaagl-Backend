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

/// <summary>openapi TableCreateRequest (docs/phase2/clipboard-tables.md).</summary>
public sealed record TableCreateRequest(
    string? Text,
    string? Format = null,
    string? Delimiter = null,
    bool? Header = null,
    string? Title = null,
    DateTime? ExpiresAt = null,
    string? Password = null,
    string? CustomSlug = null,
    bool Public = false);

/// <summary>
/// Phase 2 E2.3 instant clipboard transformers (docs/phase2/clipboard-tables.md): pasted JSON or CSV becomes a clean table link
/// (<c>app.zaa.gl/t/&lt;code&gt;</c>). Guests may create one (admin panel feature <c>guest-table</c>: pause and CAPTCHA; PD-14 counters).
/// Owner routes are owner only (404), <c>private, no-store</c>; the public routes run the content gate on every call and consume nothing.
/// Exports are returned as JSON data (file name, type, text) for the browser to save: no table bytes are served as a document.
/// </summary>
public static class TableEndpoints
{
    /// <summary>The largest pasted input (8 MiB, the configuration ceiling) as a JSON string plus overhead.</summary>
    private const long CreateBodyBytes = 20L * 1024 * 1024;

    public static void MapTables(this IEndpointRouteBuilder app)
    {
        var tables = app.MapGroup("/api/tables").WithTags("Clipboard tables");

        tables.MapPost("", async (TableCreateRequest request, ClaimsPrincipal user, HttpContext http, TableService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var (actor, refusal) = await ToolActors.ResolveWithPanelAsync(http, user, Zaagl.Modules.Links.Contracts.GuestFeatures.Table, ct);
                if (refusal is not null) return refusal;
                var created = await service.CreateAsync(
                    new TableCreator(actor!.UserId, actor.GuestCreatorKey, actor.Via, actor.TryAdmit, actor.Refund),
                    new CreateTableCommand(request.Text, request.Format, request.Delimiter, request.Header, request.Title, request.ExpiresAt,
                        request.Password, request.CustomSlug, request.Public), ct);
                return ApiResults.From(created, dto => Results.Created($"/api/tables/{dto.ResourceId}", dto));
            })
            .AllowAnonymous()
            .RejectFailedCredentials() // PD-36: a failing credential is a 401, never a guest table
            .RequireScopeWhenAuthenticated(ApiScopes.ToolsWrite)
            .RequireRateLimiting("content-write")
            .WithRequestBodyLimit(CreateBodyBytes)
            .WithSummary("Turn pasted JSON or CSV into a table link (guests: CAPTCHA guest-table when the admin panel requires it).")
            .Produces<ToolLinkCreatedDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status429TooManyRequests);

        tables.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, TableService service, IOptions<LinkOptions> links,
                CancellationToken ct, int? offset = null, int? limit = null) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.GetOwnedAsync(id, user.GetUserId(), links.Value.BaseUrl, offset, limit, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("Owner view of a table: state, columns and one page of rows (?offset=&limit=, at most 5,000).")
            .Produces<OwnerTableDto>().ProducesProblem(StatusCodes.Status404NotFound);

        tables.MapGet("/{id:guid}/export", async (Guid id, ClaimsPrincipal user, HttpContext http, TableService service, CancellationToken ct,
                string? format = null) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                return ApiResults.From(await service.ExportOwnedAsync(id, user.GetUserId(), format, ct));
            })
            .RequireScope(ApiScopes.ResourcesRead)
            .WithSummary("The whole table as CSV (formula-injection-safe) or JSON, returned as data for the browser to save.")
            .Produces<TableExportDto>().ProducesProblem(StatusCodes.Status404NotFound);

        tables.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, TableService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "private, no-store";
                var deleted = await service.DeleteAsync(id, user.GetUserId(), ct);
                return deleted.IsSuccess ? Results.NoContent() : ApiResults.Problem(deleted.Error!);
            })
            .RequireScope(ApiScopes.ToolsWrite)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var open = app.MapGroup("/api/public/tables").WithTags("Clipboard tables (public)").AllowAnonymous();

        open.MapGet("/{code}", async (string code, HttpContext http, IAccessGate gate, ILinkResolver links, IResourceRegistry registry,
                TableService service, CancellationToken ct, int? offset = null, int? limit = null) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Table, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var link = await links.GetSnapshotAsync(SlugRules.Key(code), ct);
                var resource = await registry.GetAsync(gated.ResourceId, ct);
                if (link is null || resource is null) return ApiResults.NotFound();
                return ApiResults.From(await service.DescribeAsync(gated.ResourceId, link, resource.Title, offset, limit, ct));
            })
            .RequireRateLimiting("content-read")
            .WithSummary("Table descriptor for a link holder: columns with inferred types and one page of rows (data only).")
            .Produces<TableDescriptorDto>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);

        open.MapGet("/{code}/export", async (string code, HttpContext http, IAccessGate gate, IResourceRegistry registry, TableService service,
                CancellationToken ct, string? format = null) =>
            {
                DeveloperToolsEndpoints.NoStore(http);
                var gated = await DeveloperToolsEndpoints.GateAsync(http, gate, code, LinkTargetKind.Table, ct);
                if (gated.Refusal is not null) return gated.Refusal;
                var resource = await registry.GetAsync(gated.ResourceId, ct);
                if (resource is null) return ApiResults.NotFound();
                return ApiResults.From(await service.ExportAsync(gated.ResourceId, resource.Title, format, ct));
            })
            .RequireRateLimiting("content-read")
            .WithSummary("The whole table as CSV (formula-injection-safe) or JSON, as data for the browser to save. Consumes nothing.")
            .Produces<TableExportDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
    }
}
