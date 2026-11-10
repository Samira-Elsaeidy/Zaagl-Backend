using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Identity.Infrastructure;

namespace Zaagl.Modules.Identity.Application;

/// <summary>Spec §7.4: a JWT's "sstamp" claim is validated against the user's current security stamp on
/// every request (cached 60s, so a reset/logout-everywhere takes effect within a minute rather than
/// only at the token's natural 15-minute expiry, without a DB round trip on every single call).</summary>
public sealed class SecurityStampValidator(IServiceScopeFactory scopes, IMemoryCache cache)
{
    public async Task<bool> IsCurrentAsync(Guid userId, string tokenStamp, CancellationToken ct)
    {
        var cacheKey = $"sstamp:{userId}";
        if (cache.TryGetValue(cacheKey, out string? cached))
            return string.Equals(cached, tokenStamp, StringComparison.Ordinal);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var current = await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).FirstOrDefaultAsync(ct);
        var currentStr = current.ToString();
        cache.Set(cacheKey, currentStr, TimeSpan.FromSeconds(60));
        return string.Equals(currentStr, tokenStamp, StringComparison.Ordinal);
    }
}

/// <summary>ARC-014 §2.4: the <c>sid</c> claim of a device access token names its device session; once the session
/// is revoked (DELETE /api/me/sessions/{id}, logout-everywhere, refresh reuse) the token stops working within the
/// same 60 s bound as the security stamp, instead of at its 15-minute expiry.</summary>
public sealed class DeviceSessionValidator(IServiceScopeFactory scopes, IMemoryCache cache)
{
    public static string CacheKey(Guid sessionId) => $"dsess:{sessionId}";

    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey(sessionId), out bool cached)) return cached;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var active = await db.DeviceSessions.AnyAsync(s => s.FamilyId == sessionId && s.RevokedAt == null, ct);
        cache.Set(CacheKey(sessionId), active, TimeSpan.FromSeconds(60));
        return active;
    }
}
