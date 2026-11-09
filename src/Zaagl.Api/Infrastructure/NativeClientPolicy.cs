using System.Security.Claims;
using Zaagl.Modules.Identity.Contracts;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// ARC-014 §3.1 (C11): tokens issued to the CLI or the desktop app by the device flow carry <c>amr=device</c> and a
/// <c>client_id</c> of a native client. Such a token is refused by the Interactive and Admin policies (API-key
/// management, account, billing, TOTP, device activation), so a device can never approve another device or manage
/// the account. No device tokens are issued yet (BE-024); this check is in place before they exist.
/// </summary>
public static class NativeClientPolicy
{
    public static bool IsNativeToken(ClaimsPrincipal user) =>
        user.HasClaim(NativeClientClaims.Amr, NativeClientClaims.AmrDevice)
        || user.FindAll(NativeClientClaims.ClientId).Any(c => c.Value is NativeClientIds.Cli or NativeClientIds.Desktop);
}
