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
