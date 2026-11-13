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
        // Spec §7.6: no control characters or whitespace anywhere in the URL (they were silently percent-encoded).
        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            error = "URLs cannot contain spaces or control characters.";
            return false;
        }

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
        // Spec §7.6: not Zaagl itself, and not a subdomain of it (api., app., usercontent., ...).
        if (ownHosts.Any(own => host == own || host.EndsWith("." + own, StringComparison.Ordinal)))
        {
            error = "A short link cannot point to Zaagl itself.";
            return false;
        }

        // Spec §7.6: the host is stored as punycode. AbsoluteUri keeps a Unicode host (http://аpple.com/ with a
        // Cyrillic а), which hid a homograph and made every redirect to it fail with a 500 on the non-ASCII Location.
        normalized = uri.HostNameType == UriHostNameType.Dns && uri.Host != uri.IdnHost
            ? $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped)}"
            : uri.AbsoluteUri;
        return true;
    }

    private static bool IsNonPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && EmbeddedIPv4(ip) is { } embedded) return IsNonPublic(embedded);
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19)
                || b[0] == 0
                || b[0] >= 224;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }

    /// <summary>The IPv4 address hidden in an IPv6 literal that still routes to it: deprecated IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96)
    /// and 6to4 (2002::/16). Checking the embedded address stops <c>http://[::127.0.0.1]/</c> style bypasses.</summary>
    private static IPAddress? EmbeddedIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Take(12).All(x => x == 0) && b.Skip(12).Any(x => x != 0) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] == 1))
            return new IPAddress(b[12..16]);
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return new IPAddress(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);
        return null;
    }
}
