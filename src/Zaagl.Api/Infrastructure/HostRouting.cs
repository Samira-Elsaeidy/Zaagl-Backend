using Microsoft.Net.Http.Headers;

namespace Zaagl.Api.Infrastructure;

public sealed class HostRoutingOptions
{
    public const string Section = "Hosts";

    /// <summary>Spec §1.2: the host that serves GET/HEAD /{code} and nothing else.</summary>
    public string Short { get; set; } = "localhost";

    /// <summary>Spec §1.2: the host that serves /api/*, /openapi/*, /account/* and nothing else.</summary>
    public string Api { get; set; } = "localhost";

    /// <summary>BE-005 (PD-03, ARC-008 §1, §4.1): the user-content host (<c>usercontent.zaa.gl</c>). Nginx forwards
    /// <c>/d/&lt;ticket&gt;</c> to the API's internal authorize hop <c>/internal/delivery/&lt;ticket&gt;</c> with this Host.
    /// Empty = this process serves no user-content host (development). Delivery endpoints arrive with BE-017/018.</summary>
    public string UserContent { get; set; } = "";

    /// <summary>Development only (no Nginx in front): also serve <c>/d/&lt;ticket&gt;</c> on the user-content host, which Nginx
    /// rewrites to the authorize hop <c>/internal/delivery/&lt;ticket&gt;</c> in production (ARC-008 §4.1).</summary>
    public bool RewriteDeliveryPath { get; set; }
}

/// <summary>Which of the public hosts a request belongs to (decides the header policy).</summary>
public enum HostRole { Api, Short, UserContent }

/// <summary>
/// Spec §1.2: the API process serves both Hosts:Short (redirects only) and Hosts:Api (everything else)
/// and routes by host - a redirect-shaped request on the API host, or an API request on the short host,
/// gets 421, not a silent fallthrough. /health/* is exempt (Docker/monitors call it by container name,
/// never through a real hostname). In this dev environment both config values point at the same host, so
/// existing behaviour is unaffected; the mechanism itself is real and independently tested.
/// BE-005: the user-content host (Hosts:UserContent) only ever reaches /internal/delivery/*, and that path is
/// reachable only through it; responses of the short and user-content hosts never carry Set-Cookie (spec §1.2,
/// PD-03 mitigations), and user-content responses always carry the inert header set (ARC-008 §10, defence in depth
/// behind Nginx).
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

        var host = context.Request.Host.Host;
        if (!HostRoutingDecision.IsAllowed(host, context.Request.Path, options.Value))
        {
            context.Response.StatusCode = 421; // Misdirected Request
            await context.Response.WriteAsync("Misdirected request: this path is not served on this host.");
            return;
        }

        var role = HostRoutingDecision.RoleOf(host, context.Request.Path, options.Value);
        if (role != HostRole.Api)
        {
            context.Response.OnStarting(() =>
            {
                HostHeaderPolicy.Apply(role, context.Response.Headers);
                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}

/// <summary>BE-005 response header policy per host role (pure, unit-tested).</summary>
public static class HostHeaderPolicy
{
    /// <summary>ARC-008 §10: identical to the Nginx <c>ucd-headers.conf</c> snippet.</summary>
    public const string UserContentCsp = "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'";

    public static void Apply(HostRole role, IHeaderDictionary headers)
    {
        if (role == HostRole.Api) return;

        // zaa.gl and usercontent.zaa.gl never set a cookie (spec §1.2, §4.5 rule 6; PD-03).
        headers.Remove(HeaderNames.SetCookie);

        if (role != HostRole.UserContent) return;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.ContentSecurityPolicy] = UserContentCsp;
        headers[HeaderNames.CacheControl] = "private, no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        headers["X-Robots-Tag"] = "noindex, nofollow";
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
    /// <summary>The authorize hop's path prefix on the API (ARC-008 §4.1). Two segments, so it can never be a short
    /// code (one segment); "/internal" alone stays a possible alias on the short host.</summary>
    public const string DeliveryPrefix = "/internal/delivery";

    public static bool IsAllowed(string requestHost, PathString path, HostRoutingOptions options)
    {
        if (IsUserContentHost(requestHost, options))
            return path.StartsWithSegments(DeliveryPrefix, StringComparison.OrdinalIgnoreCase)
                || (options.RewriteDeliveryPath && path.StartsWithSegments("/d", StringComparison.OrdinalIgnoreCase));
        // The authorize hop is never served on the public API or short host (ARC-008 §4.1 defence in depth).
        if (path.StartsWithSegments(DeliveryPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsRedirectShapedPath(path)
            ? HostsMatch(requestHost, options.Short)
            : HostsMatch(requestHost, options.Api);
    }

    /// <summary>Header policy role of an allowed request. With Short and Api on the same host (development), a
    /// redirect-shaped path is treated as the short host.</summary>
    public static HostRole RoleOf(string requestHost, PathString path, HostRoutingOptions options) =>
        IsUserContentHost(requestHost, options) ? HostRole.UserContent
        : IsRedirectShapedPath(path) && HostsMatch(requestHost, options.Short) ? HostRole.Short
        : HostRole.Api;

    /// <summary>Only when configured and distinct from the API and short hosts (a misconfiguration that points it
    /// at either must not take those hosts over).</summary>
    public static bool IsUserContentHost(string requestHost, HostRoutingOptions options) =>
        !string.IsNullOrWhiteSpace(options.UserContent)
        && !HostsMatch(options.UserContent, options.Api)
        && !HostsMatch(options.UserContent, options.Short)
        && HostsMatch(requestHost, options.UserContent);

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
