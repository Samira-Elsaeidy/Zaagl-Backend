using Microsoft.EntityFrameworkCore;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// Spec §7.9's admin surface plus the public report endpoint. Every route under /api/admin requires the
/// "Admin" policy (role + TOTP-backed session + Admin:AllowedNetworks, wired in Program.cs). This
/// deliberately composes across modules at the API layer (Identity for users, Links for everything
/// link/abuse-related) rather than adding cross-module service dependencies - the same pattern already
/// used by LinksEndpoints for cross-module reads.
/// </summary>
public static class AdministrationEndpoints
{
    public static void MapAdministration(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");

        // ---- Users -----------------------------------------------------------------------------
        admin.MapGet("/users", async (string? email, int page, int pageSize, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.AdminListUsersAsync(email, page == 0 ? 1 : page, pageSize == 0 ? 50 : pageSize, ct)));

        admin.MapPost("/users/{id:guid}/disable", async (Guid id, DisableUserRequest request, System.Security.Claims.ClaimsPrincipal caller,
                IdentityService identity, LinkService links, CancellationToken ct) =>
            {
                var result = await identity.AdminDisableUserAsync(caller.GetUserId(), id, request.Reason, request.DisableLinks, ct);
                if (!result.IsSuccess) return ApiResults.Problem(result.Error!);
                if (request.DisableLinks) await links.AdminSetOwnerLinksDisabledAsync(id, disabled: true, ct);
                return Results.NoContent();
            });

        admin.MapPost("/users/{id:guid}/enable", async (Guid id, EnableUserRequest request, IdentityService identity, LinkService links, CancellationToken ct) =>
            {
                var result = await identity.AdminEnableUserAsync(id, request.RestoreLinks, ct);
                if (!result.IsSuccess) return ApiResults.Problem(result.Error!);
                if (result.Value) await links.AdminSetOwnerLinksDisabledAsync(id, disabled: false, ct);
                return Results.NoContent();
            });

        // ---- Links -------------------------------------------------------------------------------
        admin.MapGet("/links/{code}", async (string code, LinkService links, CancellationToken ct) =>
            await links.AdminFindBySlugAsync(code, ct) is { } link ? Results.Ok(links.ToAdminDto(link)) : ApiResults.NotFound("Link not found."));

        admin.MapPost("/links/{code}/disable", async (string code, DisableLinkRequest request, LinkService links, TakedownService takedowns, CancellationToken ct) =>
            {
                if (request.Kind is not (DisabledKinds.Admin or DisabledKinds.Abuse)) return ApiResults.Problem(Error.Validation("kind must be \"admin\" or \"abuse\"."));
                var link = await links.AdminFindBySlugAsync(code, ct);
                if (link is null) return ApiResults.NotFound("Link not found.");
                await takedowns.TakeDownAsync(link, request.Kind, request.Reason, ct);
                return Results.NoContent();
            });

        admin.MapPost("/links/{code}/enable", async (string code, LinkService links, CancellationToken ct) =>
            ApiResults.From(await links.AdminEnableLinkAsync(code, ct)));

        admin.MapPost("/links/{code}/reinstate", async (string code, ReinstateRequest request, LinkService links, CancellationToken ct) =>
            ApiResults.From(await links.ReinstateAsync(code, request.Reason, ct)));

        // ---- Bulk takedowns ------------------------------------------------------------------------
        admin.MapPost("/takedowns", async (TakedownRequest request, LinkService links, TakedownService takedowns, CancellationToken ct) =>
            {
                var f = request.Filter;
                if (f.TargetHost is null && f.GuestCreatorKey is null && f.OwnerId is null && f.CreatedFrom is null && f.CreatedTo is null)
                    return ApiResults.Problem(Error.Validation("At least one filter is required."));

                byte[]? guestKey = f.GuestCreatorKey is null ? null : Convert.FromHexString(f.GuestCreatorKey);
                var count = await links.AdminBulkTakedownAsync(
                    f.TargetHost, guestKey, f.OwnerId, f.CreatedFrom, f.CreatedTo,
                    request.Reason, request.BlockDestination, request.DryRun, takedowns, ct);
                return Results.Ok(new TakedownResultDto(count));
            });

        // ---- Reports (admin side; public creation is mapped separately below) -------------------
        admin.MapGet("/reports", async (string? status, int page, int pageSize, ReportService reports, CancellationToken ct) =>
            {
                var (items, total) = await reports.ListAsync(status, page == 0 ? 1 : page, pageSize == 0 ? 50 : pageSize, ct);
                return Results.Ok(new Zaagl.Modules.Links.Contracts.PagedResult<ReportDto>(items.Select(ToDto).ToList(), total, page == 0 ? 1 : page, pageSize == 0 ? 50 : pageSize));
            });

        admin.MapPost("/reports/{id:guid}/resolve", async (Guid id, ResolveReportRequest request, ReportService reports, CancellationToken ct) =>
            {
                if (request.Status is not (ReportStatuses.Actioned or ReportStatuses.Dismissed))
                    return ApiResults.Problem(Error.Validation("status must be \"actioned\" or \"dismissed\"."));
                return await reports.ResolveAsync(id, request.Status, request.Resolution, ct) ? Results.NoContent() : ApiResults.NotFound("Report not found.");
            });

        // ---- Blocked destinations ------------------------------------------------------------------
        admin.MapGet("/blocked-destinations", async (LinksDbContext db, CancellationToken ct) =>
            Results.Ok(await db.BlockedDestinations.AsNoTracking().OrderByDescending(b => b.CreatedAt)
                .Select(b => new BlockedDestinationDto(b.Id, b.Kind, b.Pattern, b.Reason, b.CreatedAt)).ToListAsync(ct)));

        admin.MapPost("/blocked-destinations", async (CreateBlockedDestinationRequest request, LinksDbContext db, CancellationToken ct) =>
            {
                if (request.Kind is not (BlockedDestinationKinds.Host or BlockedDestinationKinds.HostSuffix or BlockedDestinationKinds.UrlPrefix))
                    return ApiResults.Problem(Error.Validation("Invalid kind."));
                var entry = new BlockedDestination { Kind = request.Kind, Pattern = request.Pattern, Reason = request.Reason };
                db.BlockedDestinations.Add(entry);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { return ApiResults.Problem(Error.Conflict("This pattern is already blocked.")); }
                return Results.Created($"/api/admin/blocked-destinations/{entry.Id}", new BlockedDestinationDto(entry.Id, entry.Kind, entry.Pattern, entry.Reason, entry.CreatedAt));
            });

        admin.MapDelete("/blocked-destinations/{id:guid}", async (Guid id, LinksDbContext db, CancellationToken ct) =>
            await db.BlockedDestinations.Where(b => b.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : ApiResults.NotFound("Not found."));

        // ---- Blocked aliases -----------------------------------------------------------------------
        admin.MapGet("/blocked-aliases", async (LinksDbContext db, CancellationToken ct) =>
            Results.Ok(await db.BlockedAliases.AsNoTracking().OrderByDescending(b => b.CreatedAt)
                .Select(b => new BlockedAliasDto(b.SlugKey, b.Reason, b.CreatedAt)).ToListAsync(ct)));

        admin.MapPost("/blocked-aliases", async (CreateBlockedAliasRequest request, LinksDbContext db, CancellationToken ct) =>
            {
                var key = request.Code.ToLowerInvariant();
                // Spec §7.15: blocking an alias held by a live link conflicts (409) - use link disable/takedown instead.
                if (await db.Links.AnyAsync(l => l.SlugKey == key && l.DeletedAt == null, ct))
                    return ApiResults.Problem(Error.Conflict("This alias is in use by a live link."));

                db.BlockedAliases.Add(new BlockedAlias { SlugKey = key, Reason = request.Reason });
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { return ApiResults.Problem(Error.Conflict("This alias is already blocked.")); }
                return Results.Created($"/api/admin/blocked-aliases/{key}", new BlockedAliasDto(key, request.Reason, DateTime.UtcNow));
            });

        admin.MapDelete("/blocked-aliases/{code}", async (string code, LinksDbContext db, CancellationToken ct) =>
            await db.BlockedAliases.Where(b => b.SlugKey == code.ToLowerInvariant()).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : ApiResults.NotFound("Not found."));

        // ---- Alias denylist ------------------------------------------------------------------------
        admin.MapGet("/alias-denylist", async (LinksDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AliasDenylist.AsNoTracking().OrderByDescending(e => e.CreatedAt)
                .Select(e => new AliasDenylistDto(e.Id, e.Pattern, e.Kind, e.CreatedAt)).ToListAsync(ct)));

        admin.MapPost("/alias-denylist", async (CreateAliasDenylistRequest request, LinksDbContext db, CancellationToken ct) =>
            {
                if (request.Kind is not ("exact" or "prefix")) return ApiResults.Problem(Error.Validation("kind must be \"exact\" or \"prefix\"."));
                var entry = new AliasDenylistEntry { Pattern = request.Pattern.ToLowerInvariant(), Kind = request.Kind };
                db.AliasDenylist.Add(entry);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { return ApiResults.Problem(Error.Conflict("This pattern is already denylisted.")); }
                return Results.Created($"/api/admin/alias-denylist/{entry.Id}", new AliasDenylistDto(entry.Id, entry.Pattern, entry.Kind, entry.CreatedAt));
            });

        admin.MapDelete("/alias-denylist/{id:guid}", async (Guid id, LinksDbContext db, CancellationToken ct) =>
            await db.AliasDenylist.Where(e => e.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : ApiResults.NotFound("Not found."));

        // ---- Alias freezes (spec §7.11) -------------------------------------------------------------
        // Deviation from the spec's CLI (`ops declare-restore`): exposed here as an admin endpoint too,
        // since this build has no `ops` CLI container. Functionally identical - a freeze row either way.
        admin.MapPost("/declare-restore", async (DeclareRestoreRequest request, LinksDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                var freeze = new AliasFreeze
                {
                    StartsAt = now,
                    Until = now.AddDays(30), // Aliases:RestoreFreezeDays (spec default)
                    Source = "operator",
                    Reason = request.Reason,
                };
                db.AliasFreezes.Add(freeze);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/admin/alias-freezes/{freeze.Id}", ToDto(freeze));
            });

        admin.MapGet("/alias-freezes", async (LinksDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AliasFreezes.AsNoTracking().OrderByDescending(f => f.StartsAt).Select(f => ToDto(f)).ToListAsync(ct)));

        admin.MapPost("/alias-freezes/{id:guid}/end", async (Guid id, EndFreezeRequest request, LinksDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var freeze = await db.AliasFreezes.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (freeze is null) return ApiResults.NotFound("Freeze not found.");
                freeze.EndedAt = time.GetUtcNow().UtcDateTime;
                freeze.Reason = $"{freeze.Reason} (ended early: {request.Reason})";
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            });

        // ---- Allocator view (proxies the coordinator, spec §7.9) -----------------------------------
        admin.MapGet("/allocator", async (IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct) =>
            {
                var client = httpFactory.CreateClient(nameof(AdministrationEndpoints));
                var baseUrl = config["Coordinator:BaseUrl"] ?? "http://localhost:5085";
                var token = config["Coordinator:Token"];
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v1/status");
                if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new("Bearer", token);
                try
                {
                    using var response = await client.SendAsync(request, ct);
                    var body = await response.Content.ReadAsStringAsync(ct);
                    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
                }
                catch (HttpRequestException)
                {
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }
            });

        // ---- Public report channel (not admin-gated - separate route group) ------------------------
        app.MapPost("/api/reports", async (CreateReportRequest request, ReportService reports, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Category)) return ApiResults.Problem(Error.Validation("category is required."));
                if (string.IsNullOrWhiteSpace(request.Code) && string.IsNullOrWhiteSpace(request.Url))
                    return ApiResults.Problem(Error.Validation("Either code or url is required."));

                await reports.CreateAsync(request.Code, request.Url, request.Category, request.Details, request.ReporterEmail, ct);
                return Results.Accepted();
            })
            .WithTags("Reports")
            .AllowAnonymous()
            .RequireRateLimiting("auth"); // reuses the existing per-IP limiter shape (CAPTCHA not yet wired here, see docs/TRACEABILITY.md)
    }

    private static ReportDto ToDto(Zaagl.Modules.Links.Domain.AbuseReport r) =>
        new(r.Id, r.Code, r.Url, r.Category, r.Details, r.ReporterEmail, r.Status, r.Resolution, r.CreatedAt, r.ResolvedAt);

    private static AliasFreezeDto ToDto(AliasFreeze f) => new(f.Id, f.StartsAt, f.Until, f.Source, f.Reason, f.EndedAt);
}
