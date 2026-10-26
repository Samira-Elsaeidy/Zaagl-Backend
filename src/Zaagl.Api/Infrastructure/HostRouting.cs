namespace Zaagl.Api.Infrastructure;

public sealed class HostRoutingOptions
{
    public const string Section = "Hosts";

    /// <summary>Spec §1.2: the host that serves GET/HEAD /{code} and nothing else.</summary>
    public string Short { get; set; } = "localhost";

    /// <summary>Spec §1.2: the host that serves /api/*, /openapi/*, /account/* and nothing else.</summary>
    public string Api { get; set; } = "localhost";
}

/// <summary>
/// Spec §1.2: the API process serves both Hosts:Short (redirects only) and Hosts:Api (everything else)
/// and routes by host - a redirect-shaped request on the API host, or an API request on the short host,
/// gets 421, not a silent fallthrough. /health/* is exempt (Docker/monitors call it by container name,
/// never through a real hostname). In this dev environment both config values point at the same host, so
/// existing behaviour is unaffected; the mechanism itself is real and independently tested.
/// </summary>
public sealed class HostRoutingMiddleware(RequestDelegate next, Microsoft.Extensions.Options.IOptions<HostRoutingOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        if (!HostRoutingDecision.IsAllowed(context.Request.Host.Host, context.Request.Path, options.Value))
        {
            context.Response.StatusCode = 421; // Misdirected Request
            await context.Response.WriteAsync("Misdirected request: this path is not served on this host.");
            return;
        }

        await next(context);
    }
}

/// <summary>
/// The pure decision at the heart of <see cref="HostRoutingMiddleware"/>, pulled out so it can be unit
/// tested directly (with crafted host/path/config combinations) without standing up a full ASP.NET Core
/// pipeline - proving the MECHANISM is correct independent of what this dev environment happens to
/// configure both Hosts:Short and Hosts:Api to (the same value, since there's no real second domain here).
/// </summary>
public static class HostRoutingDecision
{
    public static bool IsAllowed(string requestHost, PathString path, HostRoutingOptions options) =>
        IsRedirectShapedPath(path)
            ? HostsMatch(requestHost, options.Short)
            : HostsMatch(requestHost, options.Api);

    /// <summary>Everything this app treats as "a redirect, not an API call": the catch-all /{slug} route
    /// and nothing under /api, /openapi, /account, /hubs, /scalar.</summary>
    public static bool IsRedirectShapedPath(PathString path)
    {
        string[] apiPrefixes = ["/api", "/openapi", "/account", "/hubs", "/scalar", "/favicon.ico", "/robots.txt", "/.well-known"];
        return !apiPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
    }

    public static bool HostsMatch(string requestHost, string configuredHost) =>
        string.Equals(requestHost, configuredHost, StringComparison.OrdinalIgnoreCase)
        // Allow "host:port" in the configured value to match a bare request host (common in dev), and vice versa.
        || string.Equals(StripPort(requestHost), StripPort(configuredHost), StringComparison.OrdinalIgnoreCase);

    private static string StripPort(string host) => host.Split(':')[0];
}

public static class HostRoutingExtensions
{
    public static IApplicationBuilder UseHostRouting(this IApplicationBuilder app) =>
        app.UseMiddleware<HostRoutingMiddleware>();
}
