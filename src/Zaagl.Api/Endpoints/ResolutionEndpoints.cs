using System.Net;
using System.Text;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Resolution.Application;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

public static class ResolutionEndpoints
{
    public sealed record UnlockRequest(string? Password);

    public static void MapResolution(this IEndpointRouteBuilder app)
    {
        // The regex keeps this catch-all away from file-like paths (favicon.ico, robots.txt ...).
        app.MapMethods("/{slug:regex(^[A-Za-z0-9][A-Za-z0-9_-]{{2,63}}$)}", ["GET", "HEAD"], async (
                string slug, HttpContext http, ResolutionService resolution, IConfiguration config, CancellationToken ct) =>
            {
                var result = await resolution.ResolveAsync(slug, VisitorFrom(http), ct);
                var web = (config["Frontend:BaseUrl"] ?? "").TrimEnd('/');

                // 302, never 301 or cacheable: edits must apply at once and every click must reach analytics.
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["X-Robots-Tag"] = "noindex";

                return result.Kind switch
                {
                    ResolveKind.Redirect => Results.Redirect(result.Url!),
                    ResolveKind.Locked => Results.Redirect($"{web}/unlock/{Uri.EscapeDataString(slug)}"),
                    ResolveKind.Expired => Results.Redirect(result.Url ?? $"{web}/expired"),
                    ResolveKind.Preview => Results.Content(PreviewHtml(result.Preview!, http), "text/html; charset=utf-8"),
                    _ => Results.NotFound(),
                };
            })
            .WithTags("Resolution").ExcludeFromDescription();

        app.MapPost("/api/unlock/{slug}", async (
                string slug, UnlockRequest request, HttpContext http, ResolutionService resolution, CancellationToken ct) =>
            {
                var result = await resolution.UnlockAsync(slug, request.Password, VisitorFrom(http), ct);
                return ApiResults.From(result, url => Results.Ok(new { url }));
            })
            .WithTags("Resolution").AllowAnonymous().RequireRateLimiting("unlock");
    }

    private static VisitorRequest VisitorFrom(HttpContext http) => new(
        http.Connection.RemoteIpAddress?.ToString(),
        http.Request.Headers.UserAgent.ToString(),
        http.Request.Headers.Referer.ToString(),
        http.Request.Headers["CF-IPCountry"].ToString());

    /// <summary>Tiny page for link-preview crawlers: custom Open Graph tags. Humans who land here are sent on.</summary>
    private static string PreviewHtml(LinkPreview preview, HttpContext http)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

        var title = string.IsNullOrWhiteSpace(preview.Title) ? "Zaagl link" : preview.Title;
        var pageUrl = $"{http.Request.Scheme}://{http.Request.Host}/{preview.Slug}";

        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\">");
        html.Append($"<title>{E(title)}</title>");
        html.Append($"<meta property=\"og:title\" content=\"{E(title)}\">");
        html.Append($"<meta property=\"og:url\" content=\"{E(pageUrl)}\">");
        html.Append("<meta property=\"og:type\" content=\"website\">");
        if (!string.IsNullOrWhiteSpace(preview.Description))
            html.Append($"<meta property=\"og:description\" content=\"{E(preview.Description)}\"><meta name=\"description\" content=\"{E(preview.Description)}\">");
        if (!string.IsNullOrWhiteSpace(preview.ImageUrl))
            html.Append($"<meta property=\"og:image\" content=\"{E(preview.ImageUrl)}\"><meta name=\"twitter:card\" content=\"summary_large_image\"><meta name=\"twitter:image\" content=\"{E(preview.ImageUrl)}\">");
        html.Append($"<meta http-equiv=\"refresh\" content=\"0;url={E(preview.Destination)}\">");
        html.Append($"</head><body><a href=\"{E(preview.Destination)}\">{E(title)}</a></body></html>");
        return html.ToString();
    }
}
