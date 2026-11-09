using System.Net;
using System.Text;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// Spec §7.3 (PD-39): the short host's small static pages for links that do not redirect. No external assets;
/// <c>Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'</c>, <c>X-Content-Type-Options:
/// nosniff</c>, <c>Referrer-Policy: no-referrer</c>, <c>Cache-Control: no-store</c>. HEAD gets the same status
/// and headers without a body. Never sets a cookie.
/// </summary>
public static class StatusPages
{
    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
    public const string DefaultReportPageUrl = "https://app.zaa.gl/report";

    public static readonly IResult NotFound = new StatusPage(StatusCodes.Status404NotFound,
        "Link not found", "There is no link at this address. Check that you copied the whole address.");

    /// <summary>Quarantined (expired) with no fallback URL, and deleted links (spec §7.3: 410 "expired").</summary>
    public static readonly IResult Expired = new StatusPage(StatusCodes.Status410Gone,
        "Link expired", "This link has expired and is no longer available.");

    /// <summary>PD-43: a click-limited link whose clicks are used up (never the fallback URL).</summary>
    public static readonly IResult ClickLimitReached = new StatusPage(StatusCodes.Status410Gone,
        "Click limit reached", "This link has reached its click limit.");

    /// <summary>Disabled by its owner or an administrator.</summary>
    public static readonly IResult Disabled = new StatusPage(StatusCodes.Status410Gone,
        "Link disabled", "This link has been disabled and no longer works.");

    /// <summary>Disabled kinds <c>abuse</c> and <c>safety</c>: the acceptable-use page, linking to the report page.</summary>
    public static IResult AcceptableUse(string? reportPageUrl)
    {
        var report = Uri.TryCreate(reportPageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.AbsoluteUri
            : DefaultReportPageUrl;
        return new StatusPage(StatusCodes.Status410Gone,
            "Link disabled",
            "This link was disabled for violating the acceptable use policy.",
            $"If you think this is a mistake, <a href=\"{WebUtility.HtmlEncode(report)}\" rel=\"noreferrer\">contact us through the report page</a>.");
    }

    private sealed class StatusPage(int status, string title, string message, string? extraHtml = null) : IResult
    {
        private readonly byte[] _body = Encoding.UTF8.GetBytes(Render(title, message, extraHtml));

        public async Task ExecuteAsync(HttpContext http)
        {
            var response = http.Response;
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsHead(http.Request.Method)) return;
            response.ContentLength = _body.Length;
            await response.Body.WriteAsync(_body, http.RequestAborted);
        }
    }

    private static string Render(string title, string message, string? extraHtml)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\">"
            + $"<title>{E(title)} - Zaagl</title>"
            + "<style>body{margin:0;font:16px/1.5 system-ui,sans-serif;color:#1f2328;background:#f6f8fa}"
            + "main{max-width:32rem;margin:15vh auto;padding:0 1rem}h1{font-size:1.5rem;margin:0 0 .5rem}"
            + "a{color:#0969da}@media (prefers-color-scheme:dark){body{color:#e6edf3;background:#0d1117}a{color:#4493f8}}</style>"
            + $"</head><body><main><h1>{E(title)}</h1><p>{E(message)}</p>"
            + (extraHtml is null ? "" : $"<p>{extraHtml}</p>")
            + "</main></body></html>";
    }
}
