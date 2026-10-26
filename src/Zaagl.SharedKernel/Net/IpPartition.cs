using System.Net;
using System.Net.Sockets;

namespace Zaagl.SharedKernel.Net;

/// <summary>
/// Spec §7.3: the one partition key used by every IP-based limiter in the service (redirects, auth,
/// guest quotas later). IPv4 partitions by the full address; IPv6 partitions by the /64 prefix, so one
/// IPv6 allocation (which can hand a client billions of addresses) cannot evade a per-address limit.
/// </summary>
public static class IpPartition
{
    public static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.AddressFamily == AddressFamily.InterNetwork) return "v4:" + address;
        if (address.IsIPv4MappedToIPv6) return "v4:" + address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16) return "v6:" + address; // unexpected shape, fail open to the full address

        Span<byte> prefix = stackalloc byte[16];
        bytes.AsSpan(0, 8).CopyTo(prefix); // top 64 bits
        // bottom 64 bits stay zero
        return "v6/64:" + new IPAddress(prefix);
    }

    /// <summary>Spec §7.12: the two wider IPv6 quota prefixes (/56, /48), so one allocation can't evade
    /// the per-/64 guest quota by rotating addresses within it. Null for IPv4 (not needed there).</summary>
    public static string? WidePrefixKey(IPAddress? address, int bits)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
            return null;

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16) return null;

        Span<byte> prefix = stackalloc byte[16];
        var fullBytes = bits / 8;
        bytes.AsSpan(0, fullBytes).CopyTo(prefix);
        return $"v6/{bits}:" + new IPAddress(prefix);
    }
}
