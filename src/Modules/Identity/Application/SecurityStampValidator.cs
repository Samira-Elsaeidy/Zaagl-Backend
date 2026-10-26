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
