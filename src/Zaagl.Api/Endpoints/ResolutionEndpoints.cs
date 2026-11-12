using System.Net;
using System.Text;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Resolution.Application;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Endpoints;

public static class ResolutionEndpoints
{
    public sealed record UnlockRequest(string? Password);

    /// <summary>Spec §7.3 (F-09): Cache-Control on every redirect of the short host.</summary>
    public const string RedirectCacheControl = "private, max-age=0";

    /// <summary>Spec §7.3: where the acceptable-use page sends visitors (default https://app.zaa.gl/report).</summary>
    public const string ReportPageUrlKey = "Links:ReportPageUrl";

    // Mirrors Links' DisabledKinds (Domain, not visible here); the strings are part of spec §4.3.
    private const string DisabledKindAbuse = "abuse";
    private const string DisabledKindSafety = "safety";

    /// <summary>Spec §7.3 route shape (the leading character stays alphanumeric, as every slug's does).</summary>
    public const string RedirectRoute = "/{slug:regex(^[A-Za-z0-9][A-Za-z0-9_-]{{3,63}}$)}";

    public static void MapResolution(this IEndpointRouteBuilder app)
    {
        // Spec §7.3 (BE-031): one segment of 4-64 characters. Anything else (1-3 characters, multi-segment,
        // file-like paths such as favicon.ico) matches no route and gets 404 before any I/O. Custom slugs are at
        // least SlugRules.MinCustomLength (4) and generated codes at least CodeLengths.MinLength (5) characters.
        app.MapMethods(RedirectRoute, ["GET", "HEAD"], async (
                string slug, HttpContext http, IRedirectRateLimiter limiter, IConfiguration config, CancellationToken ct) =>
            {
                // Spec §7.3 (F-09): 302, never 301; "private, max-age=0" so edits apply at once and every click
                // reaches analytics. The static 404/410 pages below override this with no-store.
                http.Response.Headers.CacheControl = RedirectCacheControl;
                http.Response.Headers["X-Robots-Tag"] = "noindex";

                // Spec §7.3: 300/min per IP partition and tiered miss budgets; HEAD counts like GET. Checked
                // before the lookup, and ResolutionService (database scope) is only resolved once it passes.
                var address = http.Connection.RemoteIpAddress;
                var decision = limiter.TryAcquire(address);
                if (!decision.Allowed)
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Text("Too many requests.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status429TooManyRequests);
                }

                var resolution = http.RequestServices.GetRequiredService<ResolutionService>();
                // Spec §7.3 "HEAD never counts": no click consumed, no analytics recorded (RD-01).
                var result = await resolution.ResolveAsync(slug, VisitorFrom(http), ct, countsAsClick: !HttpMethods.IsHead(http.Request.Method));
                if (result.Kind is ResolveKind.NotFound or ResolveKind.Expired or ResolveKind.Deleted or ResolveKind.Disabled or ResolveKind.ClickLimitReached)
                    limiter.RecordMiss(address);

                var web = (config["Frontend:BaseUrl"] ?? "").TrimEnd('/');
                return result.Kind switch
                {
                    ResolveKind.Redirect => Results.Redirect(result.Url!),
                    // ARC-002 §7 / PD-02: content links 302 to the viewer on the app host; no Set-Cookie, no fragment
                    // added (the browser keeps #k=), Cache-Control private, max-age=0 as set above.
                    ResolveKind.Content => Results.Redirect($"{web}{result.Url}"),
                    ResolveKind.Locked => Results.Redirect($"{web}/unlock/{Uri.EscapeDataString(slug)}"),
                    // PD-42: an expired link with an owner-set fallback URL goes there; otherwise the 410 page.
                    ResolveKind.Expired when !string.IsNullOrEmpty(result.Url) => Results.Redirect(result.Url),
                    ResolveKind.Expired or ResolveKind.Deleted => StatusPages.Expired,
                    ResolveKind.ClickLimitReached => StatusPages.ClickLimitReached, // PD-43: never the fallback
                    ResolveKind.Disabled => result.DisabledKind is DisabledKindAbuse or DisabledKindSafety
                        ? StatusPages.AcceptableUse(config[ReportPageUrlKey])
                        : StatusPages.Disabled,
                    ResolveKind.Preview => Results.Content(PreviewHtml(result.Preview!, http), "text/html; charset=utf-8"),
                    _ => StatusPages.NotFound,
                };
            })
            .WithTags("Resolution").ExcludeFromDescription();

        // Every other GET/HEAD that matched no route (a 1-3 character slug such as the legacy "sam", a multi-segment or
        // file-like path, a reserved word) used to get an empty-body 404. Browsers navigating the short host now get the
        // same branded "Link not found" page; API, hub and health paths keep their plain 404.
        app.MapMethods("/{**path:nonfile}", ["GET", "HEAD"], async (HttpContext http) =>
        {
            var path = http.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/health"))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await StatusPages.NotFound.ExecuteAsync(http);
        }).ExcludeFromDescription();

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
