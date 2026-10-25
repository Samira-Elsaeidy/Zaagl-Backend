using System.Text.RegularExpressions;

namespace Zaagl.Modules.Links.Application;

public static partial class SlugRules
{
    /// <summary>4 per spec §4.5 rule 4 (raised from 3: a 3-character custom slug is no longer accepted).</summary>
    public const int MinCustomLength = 4;
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{3,63}$")]
    private static partial Regex Format();

    public static bool IsValidFormat(string? slug) => slug is not null && Format().IsMatch(slug);

    public static string Key(string slug) => slug.ToLowerInvariant();

    /// <summary>
    /// First path segments a slug must never shadow: this app's own routes, plus the spec v9.4 §4.5
    /// rule 3 list (routes for products not yet built here - kept anyway, so adding them later never
    /// breaks a live slug).
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        // This app's own routes.
        "unlock", "expired", "p", "f", "i", "hook", "drop", "scalar", "hubs", "auth",
        "sign-in", "sign-up", "dashboard", "links", "settings", "stats", "public",
        "manifest.json", "_next", "404", "500", "new",

        // Spec v9.4 §4.5 rule 3.
        "api", "app", "www", "health", "status", "swagger", "openapi", "docs", "admin", "static", "assets",
        "favicon.ico", "robots.txt", "sitemap.xml", "ads.txt", ".well-known", "security.txt", "account",
        "login", "signin", "signup", "register", "logout", "verify", "password", "confirm-email",
        "reset-password", "support", "help", "contact", "abuse", "report", "preview", "terms", "privacy",
        "legal", "dmca", "about", "pricing", "blog", "zaagl", "files", "file", "snippets", "snippet",
        "images", "raw", "embed", "download", "upload",
    };

    public static bool IsReserved(string slug) => Reserved.Contains(slug);
}
