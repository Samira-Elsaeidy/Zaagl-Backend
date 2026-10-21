using System.Net;
using System.Net.Sockets;

namespace Zaagl.Modules.Links.Application;

/// <summary>Syntactic checks on user-supplied URLs: scheme, host and loop protection. Reputation is checked separately.</summary>
public static class UrlSafety
{
    public const int MaxLength = 2048;

    private static readonly string[] InternalSuffixes = [".local", ".internal", ".localhost", ".lan", ".home.arpa"];

    public static bool TryNormalize(string? input, IReadOnlyCollection<string> ownHosts, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        if (string.IsNullOrWhiteSpace(input)) { error = "URL is required."; return false; }
        var trimmed = input.Trim();
        if (trimmed.Length > MaxLength) { error = $"URL must be at most {MaxLength} characters."; return false; }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Only absolute http(s) URLs are allowed.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "URLs with embedded credentials are not allowed."; return false; }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || InternalSuffixes.Any(host.EndsWith))
        {
            error = "Local and internal hosts are not allowed.";
            return false;
        }
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsNonPublic(ip))
        {
            error = "Private and loopback addresses are not allowed.";
            return false;
        }
        if (ownHosts.Contains(host))
        {
            error = "A short link cannot point to Zaagl itself.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool IsNonPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || b[0] == 0
                || b[0] >= 224;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }
}
