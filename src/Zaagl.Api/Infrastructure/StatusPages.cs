using System.Net;
using System.Text;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// Spec §7.3 (PD-39): the short host's small static pages for links that do not redirect, now branded (inline
/// pigeon mark, light/dark through prefers-color-scheme), localized (en/ar from Accept-Language, RTL for ar) and
/// carrying a link back to the app. No external assets and no script; style is one inline block under
/// <c>Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'</c> (unchanged policy),
/// <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c>, <c>Cache-Control: no-store</c>.
/// HEAD gets the same status and headers without a body. Never sets a cookie.
/// </summary>
public static class StatusPages
{
    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
    public const string DefaultReportPageUrl = "https://app.zaa.gl/report";
    public const string DefaultAppUrl = "https://app.zaa.gl";

    public static readonly IResult NotFound = new StatusPage(StatusCodes.Status404NotFound,
        new("Link not found", "There is no link at this address. Check that you copied the whole address.",
            "الرابط غير موجود", "لا يوجد رابط في هذا العنوان. تأكد من نسخ العنوان بالكامل."));

    /// <summary>Quarantined (expired) with no fallback URL, and deleted links (spec §7.3: 410 "expired").</summary>
    public static readonly IResult Expired = new StatusPage(StatusCodes.Status410Gone,
        new("Link expired", "This link has expired and is no longer available.",
            "انتهت صلاحية الرابط", "انتهت صلاحية هذا الرابط ولم يعد متاحًا."));

    /// <summary>PD-43: a click-limited link whose clicks are used up (never the fallback URL).</summary>
    public static readonly IResult ClickLimitReached = new StatusPage(StatusCodes.Status410Gone,
        new("Click limit reached", "This link has reached its click limit.",
            "تم بلوغ حد النقرات", "وصل هذا الرابط إلى الحد الأقصى من النقرات."));

    /// <summary>Disabled by its owner or an administrator.</summary>
    public static readonly IResult Disabled = new StatusPage(StatusCodes.Status410Gone,
        new("Link disabled", "This link has been disabled and no longer works.",
            "الرابط معطّل", "تم تعطيل هذا الرابط ولم يعد يعمل."));

    /// <summary>Disabled kinds <c>abuse</c> and <c>safety</c>: the acceptable-use page, linking to the report page.</summary>
    public static IResult AcceptableUse(string? reportPageUrl)
    {
        var report = Uri.TryCreate(reportPageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.AbsoluteUri
            : DefaultReportPageUrl;
        return new StatusPage(StatusCodes.Status410Gone,
            new("Link disabled", "This link was disabled for violating the acceptable use policy.",
                "الرابط معطّل", "تم تعطيل هذا الرابط لمخالفته سياسة الاستخدام المقبول.",
                ReportUrl: report));
    }

    private sealed record Content(string EnTitle, string EnMessage, string ArTitle, string ArMessage, string? ReportUrl = null);

    private sealed class StatusPage(int status, Content content) : IResult
    {
        // Rendered per request: the language and the app URL (configuration) vary. The page is tiny.
        public async Task ExecuteAsync(HttpContext http)
        {
            var response = http.Response;
            var arabic = PrefersArabic(http.Request.Headers.AcceptLanguage.ToString());
            var appUrl = AppUrl(http.RequestServices.GetService<IConfiguration>());
            var body = Encoding.UTF8.GetBytes(Render(content, arabic, appUrl));

            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.CacheControl = "no-store";
            response.Headers.Vary = "Accept-Language";
            response.Headers["Content-Language"] = arabic ? "ar" : "en";
            if (HttpMethods.IsHead(http.Request.Method)) return;
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, http.RequestAborted);
        }
    }

    /// <summary>The first Arabic or English tag in Accept-Language wins (browsers list preferences in order); anything else is English.</summary>
    public static bool PrefersArabic(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)) return false;
        foreach (var part in acceptLanguage.Split(','))
        {
            var tag = part.Split(';')[0].Trim();
            if (IsLanguage(tag, "ar")) return true;
            if (IsLanguage(tag, "en")) return false;
        }
        return false;
    }

    private static bool IsLanguage(string tag, string language) =>
        tag.StartsWith(language, StringComparison.OrdinalIgnoreCase) && (tag.Length == 2 || tag[2] == '-');

    private static string AppUrl(IConfiguration? config)
    {
        var configured = config?["Frontend:BaseUrl"];
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.GetLeftPart(UriPartial.Authority)
            : DefaultAppUrl;
    }

    private const string PigeonMark = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"20 20 260 280\" width=\"40\" height=\"40\" role=\"img\" aria-label=\"Zaagl\"><g id=\"pigeon-mark\"><polygon points=\"240,105 270,118 240,126\" fill=\"#0f172a\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"190,80 225,85 240,105 205,115\" fill=\"#38bdf8\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"240,105 270,118 240,126 205,115\" fill=\"#0284c7\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"190,80 205,115 170,135 150,108\" fill=\"#0369a1\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"205,115 240,126 220,165 170,135\" fill=\"#0284c7\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"170,135 220,165 180,205 135,170\" fill=\"#0369a1\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"30,25 90,45 75,90\" fill=\"#7dd3fc\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"90,45 140,70 115,105 75,90\" fill=\"#38bdf8\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"140,70 170,88 145,118 115,105\" fill=\"#0284c7\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"75,90 115,105 85,155 50,125\" fill=\"#0284c7\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"115,105 145,118 110,175 85,155\" fill=\"#0369a1\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"145,118 150,108 135,170 110,175\" fill=\"#1e3a8a\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"135,170 180,205 150,225 110,195\" fill=\"#1e3a8a\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"135,170 110,195 70,275 100,240\" fill=\"#0284c7\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /><polygon points=\"110,195 150,225 88,290 70,275\" fill=\"#0f172a\" stroke=\"#0f172a\" stroke-width=\"2.5\" stroke-linejoin=\"round\" /></g></svg>";

    private const string Css =
        ":root{--bg:#f6f8fa;--fg:#1f2328;--muted:#59636e;--card:#fff;--line:#d1d9e0;--link:#0969da;--accent:#0284c7}"
        + "@media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--card:#151b23;--line:#30363d;--link:#4493f8;--accent:#38bdf8}}"
        + "*{box-sizing:border-box}body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:1rem;"
        + "font:16px/1.6 system-ui,-apple-system,'Segoe UI',Roboto,'Noto Sans Arabic',sans-serif;color:var(--fg);background:var(--bg)}"
        + "main{width:100%;max-width:30rem;background:var(--card);border:1px solid var(--line);border-top:4px solid var(--accent);"
        + "border-radius:12px;padding:2rem 1.75rem;text-align:center}"
        + ".brand{display:inline-flex;align-items:center;gap:.6rem;margin-bottom:1.25rem;font-weight:800;font-size:1.35rem;color:var(--fg);text-decoration:none}"
        + "h1{font-size:1.5rem;line-height:1.3;margin:0 0 .5rem}p{margin:.5rem 0;color:var(--muted)}"
        + "a{color:var(--link)}.back{display:inline-block;margin-top:1.25rem;padding:.55rem 1.1rem;border-radius:8px;background:var(--accent);color:#fff;text-decoration:none;font-weight:600}"
        + ".back:hover{opacity:.9}";

    private static string Render(Content c, bool arabic, string appUrl)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var title = arabic ? c.ArTitle : c.EnTitle;
        var message = arabic ? c.ArMessage : c.EnMessage;
        var brand = arabic ? "زاجل" : "Zaagl";
        var back = arabic ? "الذهاب إلى زاجل" : "Go to Zaagl";

        var extra = "";
        if (c.ReportUrl is not null)
        {
            var link = E(c.ReportUrl);
            extra = arabic
                ? $"<p>إذا كنت تعتقد أن هذا خطأ، <a href=\"{link}\" rel=\"noreferrer\">تواصل معنا عبر صفحة الإبلاغ</a>.</p>"
                : $"<p>If you think this is a mistake, <a href=\"{link}\" rel=\"noreferrer\">contact us through the report page</a>.</p>";
        }

        return $"<!doctype html><html lang=\"{(arabic ? "ar" : "en")}\" dir=\"{(arabic ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\">"
            + "<meta name=\"color-scheme\" content=\"light dark\">"
            + $"<title>{E(title)} - {brand}</title><style>{Css}</style></head><body><main>"
            + $"<a class=\"brand\" href=\"{E(appUrl)}\" rel=\"noreferrer\">{PigeonMark}<span>{brand}</span></a>"
            + $"<h1>{E(title)}</h1><p>{E(message)}</p>{extra}"
            + $"<a class=\"back\" href=\"{E(appUrl)}\" rel=\"noreferrer\">{back}</a>"
            + "</main></body></html>";
    }
}
