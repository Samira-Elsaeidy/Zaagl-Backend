using System.Text.RegularExpressions;

namespace Zaagl.Modules.Links.Application;

public static partial class SlugRules
{
    public const int MinCustomLength = 3;
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{2,63}$")]
    private static partial Regex Format();

    public static bool IsValidFormat(string? slug) => slug is not null && Format().IsMatch(slug);

    public static string Key(string slug) => slug.ToLowerInvariant();

    /// <summary>First path segments owned by the app (API, web routes, well-known files) that a slug must never shadow.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "scalar", "openapi", "hubs", "unlock", "expired", "p", "f", "i", "hook", "drop",
        "app", "www", "admin", "login", "logout", "signin", "sign-in", "signup", "sign-up", "register", "auth",
        "pricing", "billing", "dashboard", "links", "settings", "stats", "static", "assets", "public",
        "favicon.ico", "robots.txt", "sitemap.xml", "manifest.json", "_next", "404", "500",
        "about", "terms", "privacy", "support", "help", "abuse", "report", "new", "docs", "status",
    };

    public static bool IsReserved(string slug) => Reserved.Contains(slug);
}
