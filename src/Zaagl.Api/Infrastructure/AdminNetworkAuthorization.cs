using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Zaagl.Api.Infrastructure;

/// <summary>Spec §7.4: /api/admin/* only accepts requests from Admin:AllowedNetworks (office/VPN CIDR
/// ranges in production). DEV-MODE SHORTCUT: an empty list means "allow every address" so this works
/// out of the box on a dev machine with no VPN - production MUST configure real ranges, since an empty
/// list there would defeat the whole point of this check.</summary>
public sealed class AdminNetworkOptions
{
    public const string Section = "Admin";
    public List<string> AllowedNetworks { get; set; } = [];
}

public sealed class AdminNetworkRequirement : IAuthorizationRequirement;

public sealed class AdminNetworkHandler(IOptions<AdminNetworkOptions> options, IHttpContextAccessor http)
    : AuthorizationHandler<AdminNetworkRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminNetworkRequirement requirement)
    {
        var ranges = options.Value.AllowedNetworks;
        var address = http.HttpContext?.Connection.RemoteIpAddress;

        if (ranges.Count == 0 || (address is not null && ranges.Any(r => IsInRange(address, r))))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    private static bool IsInRange(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefixLength))
            return false;
        if (address.AddressFamily != network.AddressFamily) return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
            if (addressBytes[i] != networkBytes[i]) return false;

        if (remainingBits == 0) return true;
        var mask = (byte)~(0xFF >> remainingBits);
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
