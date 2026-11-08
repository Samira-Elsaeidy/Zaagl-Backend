using System.Security.Claims;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this IEndpointRouteBuilder app)
    {
        // range: live | 24h | 7d | 30d | custom (with from / to as ISO-8601 timestamps)
        app.MapGet("/api/links/{id:guid}/analytics", async (
                Guid id, string? range, DateTime? from, DateTime? to,
                ClaimsPrincipal user, ILinkDirectory links, IPlanProvider plans, IAnalyticsQueries analytics, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                if (await links.FindOwnedAsync(userId, id, ct) is null) return ApiResults.NotFound("Link not found.");
                if (!TryParseRange(range, out var parsed))
                    return ApiResults.Problem(Error.Validation("range must be one of: live, 24h, 7d, 30d, custom."));

                var limits = await plans.GetLimitsAsync(userId, ct);
                var overview = await analytics.GetOverviewAsync(id, parsed, from, to, limits.AnalyticsRetentionDays, ct);
                return Results.Ok(overview);
            })
            .WithTags("Analytics").RequireAuthorization();

        // Individual human clicks ("View events"). Same range rules and plan retention as the overview.
        // Never returns an IP address or the visitor hash.
        app.MapGet("/api/links/{id:guid}/analytics/events", async (
                Guid id, string? range, DateTime? from, DateTime? to,
                string? country, string? device, string? browser, string? referrer,
                string? order, int? limit, string? cursor,
                ClaimsPrincipal user, ILinkDirectory links, IPlanProvider plans, IAnalyticsQueries analytics, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                if (await links.FindOwnedAsync(userId, id, ct) is null) return ApiResults.NotFound("Link not found.");
                if (!TryParseRange(range, out var parsed))
                    return ApiResults.Problem(Error.Validation("range must be one of: live, 24h, 7d, 30d, custom."));
                if (order is not (null or "" or "newest" or "oldest"))
                    return ApiResults.Problem(Error.Validation("order must be newest or oldest."));
                if (limit is < 1 or > 100)
                    return ApiResults.Problem(Error.Validation("limit must be between 1 and 100."));
                if (country is { Length: > 0 } && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
                    return ApiResults.Problem(Error.Validation("country must be a two-letter country code."));
                if (new[] { device, browser, referrer }.Any(v => v is { Length: > 255 }))
                    return ApiResults.Problem(Error.Validation("Filter values must be at most 255 characters."));
                if (cursor is not null && !ClickEventCursor.IsValid(cursor))
                    return ApiResults.Problem(Error.Validation("cursor is not valid. Start again from the first page."));

                var limits = await plans.GetLimitsAsync(userId, ct);
                var page = await analytics.GetEventsAsync(id, new ClickEventQuery(
                    parsed, from, to, limits.AnalyticsRetentionDays,
                    country, device, browser, referrer,
                    OldestFirst: order == "oldest", Limit: limit ?? 50, Cursor: cursor), ct);
                return Results.Ok(page);
            })
            .WithTags("Analytics").RequireAuthorization();

        // Clicks grouped by UTM source, medium, campaign, term and content of the destination the visitor was sent to.
        app.MapGet("/api/links/{id:guid}/analytics/utm", async (
                Guid id, string? range, DateTime? from, DateTime? to,
                ClaimsPrincipal user, ILinkDirectory links, IPlanProvider plans, IAnalyticsQueries analytics, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                if (await links.FindOwnedAsync(userId, id, ct) is null) return ApiResults.NotFound("Link not found.");
                if (!TryParseRange(range, out var parsed))
                    return ApiResults.Problem(Error.Validation("range must be one of: live, 24h, 7d, 30d, custom."));

                var limits = await plans.GetLimitsAsync(userId, ct);
                return Results.Ok(await analytics.GetUtmReportAsync(id, parsed, from, to, limits.AnalyticsRetentionDays, ct));
            })
            .WithTags("Analytics").RequireAuthorization();

        // Same overview summed over everything the user owns (dashboard cards and the Analytics page).
        app.MapGet("/api/analytics/overview", async (
                string? range, DateTime? from, DateTime? to,
                ClaimsPrincipal user, IPlanProvider plans, IAnalyticsQueries analytics, CancellationToken ct) =>
            {
                if (!TryParseRange(range, out var parsed))
                    return ApiResults.Problem(Error.Validation("range must be one of: live, 24h, 7d, 30d, custom."));

                var userId = user.GetUserId();
                var limits = await plans.GetLimitsAsync(userId, ct);
                return Results.Ok(await analytics.GetOwnerOverviewAsync(userId, parsed, from, to, limits.AnalyticsRetentionDays, ct));
            })
            .WithTags("Analytics").RequireAuthorization();

        // Read-only stats page that a link owner can opt into per link (PublicStats). Never exposes the destination.
        app.MapGet("/api/public/stats/{slug}", async (
                string slug, ILinkDirectory links, IAnalyticsQueries analytics, CancellationToken ct) =>
            {
                if (await links.FindPublicStatsAsync(slug, ct) is not { } link)
                    return ApiResults.NotFound("No public stats for this link.");

                var overview = await analytics.GetOverviewAsync(link.Id, AnalyticsRange.Last30Days, null, null, 30, ct);
                return Results.Ok(new { link.Slug, link.Title, overview });
            })
            .WithTags("Analytics").AllowAnonymous();
    }

    public static void MapArchive(this IEndpointRouteBuilder app)
    {
        // Spec §7.9: GET /api/archive, GET /api/archive/{linkId}/stats. Populated by the purge job
        // (phase 5/6, not built yet) - these endpoints are correct now and return an empty list until then.
        app.MapGet("/api/archive", async (
                string? cursor, int? limit, ClaimsPrincipal user, IArchiveQueries archive, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                return Results.Ok(await archive.ListAsync(userId, limit ?? 50, cursor, ct));
            })
            .WithTags("Analytics").RequireAuthorization();

        app.MapGet("/api/archive/{linkId:guid}/stats", async (
                Guid linkId, ClaimsPrincipal user, IArchiveQueries archive, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                if (await archive.FindAsync(userId, linkId, ct) is null) return ApiResults.NotFound("Archived link not found.");
                return Results.Ok(await archive.GetStatsAsync(linkId, ct));
            })
            .WithTags("Analytics").RequireAuthorization();
    }

    private static bool TryParseRange(string? value, out AnalyticsRange range)
    {
        range = AnalyticsRange.Last7Days;
        switch (value?.ToLowerInvariant())
        {
            case null or "" or "7d": return true;
            case "live": range = AnalyticsRange.Live; return true;
            case "24h": range = AnalyticsRange.Last24Hours; return true;
            case "30d": range = AnalyticsRange.Last30Days; return true;
            case "custom": range = AnalyticsRange.Custom; return true;
            default: return false;
        }
    }
}
