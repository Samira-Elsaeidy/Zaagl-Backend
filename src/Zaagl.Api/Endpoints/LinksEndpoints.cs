using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Api.Endpoints;

/// <summary>A link plus its all-time human click count, as shown in the link library.</summary>
public sealed record LinkListItem(LinkDto Link, long Clicks);

public sealed record RenewRequest(DateTime? ExpiresAt);

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

        links.MapPost("", async (
                LinkInput input, ClaimsPrincipal user, LinkService service, HttpContext http,
                GuestKeyProvider guestKey, GuestRateLimiter guestLimiter, IOptions<GuestOptions> guestOptions,
                ICaptchaVerifier captcha, IConfiguration config,
                CancellationToken ct) =>
            {
                // Spec §7.2: an Idempotency-Key (1-128 printable ASCII) makes a retried create safe to resend.
                var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
                if (string.IsNullOrEmpty(idempotencyKey)) idempotencyKey = null;

                if (user.Identity?.IsAuthenticated == true)
                {
                    return ApiResults.From(await service.CreateAsync(user.GetUserId(), input, ct, idempotencyKey),
                        dto => Results.Created($"/api/links/{dto.Id}", dto));
                }

                // Spec §7.12: no credentials -> a guest link, identified only by its IP partition.
                var opts = guestOptions.Value;
                if (!opts.Enabled)
                    return ApiResults.Problem(Error.ServiceUnavailable("Guest link creation is currently disabled."));

                var captchaToken = http.Request.Headers["Captcha-Token"].ToString();
                var frontendHost = Uri.TryCreate(config["Frontend:BaseUrl"], UriKind.Absolute, out var frontendUri) ? frontendUri.Host : "";
                var verified = await captcha.VerifyAsync(
                    string.IsNullOrEmpty(captchaToken) ? null : captchaToken, "guest-link", frontendHost, ct);
                if (!verified.IsSuccess) return ApiResults.Problem(verified.Error!);

                var address = http.Connection.RemoteIpAddress;
                var partition = IpPartition.Key(address);
                if (!guestLimiter.TryGlobalCeiling(partition, opts.GlobalPerHour))
                    return ApiResults.Problem(new Error("guest_creation_paused", "Guest link creation is temporarily paused for this network. Try again shortly."));
                if (!guestLimiter.TryPerMinute(partition, opts.PerMinute))
                    return ApiResults.Problem(Error.TooManyRequests("Too many requests. Try again in a minute."));

                var creatorKey = GuestIdentity.DeriveKey(guestKey.Key, partition);
                var checks = new List<GuestQuotaCheck> { new("creator-key", creatorKey, opts.LinksPerDay) };
                if (IpPartition.WidePrefixKey(address, 56) is { } prefix56)
                    checks.Add(new("/56", GuestIdentity.DeriveKey(guestKey.Key, prefix56), opts.LinksPerDayPer56));
                if (IpPartition.WidePrefixKey(address, 48) is { } prefix48)
                    checks.Add(new("/48", GuestIdentity.DeriveKey(guestKey.Key, prefix48), opts.LinksPerDayPer48));

                return ApiResults.From(await service.CreateGuestAsync(creatorKey, checks, input, ct, idempotencyKey),
                    dto => Results.Created($"/api/links/{dto.Id}", dto));
            })
            .AllowAnonymous()
            .RequireRateLimiting("link-write");

        links.MapPost("/{id:guid}/renew", async (Guid id, RenewRequest request, ClaimsPrincipal user, LinkService service, CancellationToken ct) =>
                ApiResults.From(await service.RenewAsync(user.GetUserId(), id, request.ExpiresAt, ct)))
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
