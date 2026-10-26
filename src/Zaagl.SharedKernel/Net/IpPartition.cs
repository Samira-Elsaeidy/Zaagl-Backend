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
}
