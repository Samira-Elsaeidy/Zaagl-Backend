using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// Spec §7.13 (BE-030): which hops may set the client IP. Only the exact addresses in <see cref="KnownProxies"/>
/// (the reverse proxy and the web/BFF container) and, on staging only, the access gate's ranges in
/// <see cref="KnownNetworks"/> are trusted. With nothing configured no forwarded header is trusted at all,
/// and every request is partitioned by the address of the connection that reached the API.
/// </summary>
public sealed class NetworkOptions
{
    public const string Section = "Network";

    /// <summary>Exact IP addresses (IPv4 or IPv6), never subnets.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges (e.g. <c>203.0.113.0/24</c>). Staging access gate only.</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>How many X-Forwarded-For entries are processed, right to left (production 2, staging 3).</summary>
    public int ForwardLimit { get; set; } = 2;
}

public static class ForwardedHeadersSetup
{
    /// <summary>Applies <paramref name="network"/> to <paramref name="options"/>. Invalid entries fail startup
    /// rather than being skipped, so a typo can never silently widen or empty the trusted set.</summary>
    public static void Apply(ForwardedHeadersOptions options, NetworkOptions network)
    {
        if (network.ForwardLimit < 1)
            throw new InvalidOperationException("Network:ForwardLimit must be at least 1.");

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = network.ForwardLimit;

        // The framework trusts loopback by default; only what is configured may be trusted here.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var raw in network.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var value = raw.Trim();
            if (value.Contains('/') || !IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException($"Network:KnownProxies entry '{value}' is not an exact IP address.");
            options.KnownProxies.Add(address);
        }

        foreach (var raw in network.KnownNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var value = raw.Trim();
            if (!System.Net.IPNetwork.TryParse(value, out var range))
                throw new InvalidOperationException($"Network:KnownNetworks entry '{value}' is not a valid CIDR range.");
            options.KnownIPNetworks.Add(range);
        }

        // ForwardedHeadersMiddleware trusts EVERY sender when both lists are empty (the Phase 0 defect, F-02),
        // so with nothing configured forwarding is switched off instead.
        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            options.ForwardedHeaders = ForwardedHeaders.None;
    }
}
