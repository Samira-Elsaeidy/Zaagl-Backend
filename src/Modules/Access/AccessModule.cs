using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Modules.Access.Application;
using Zaagl.Modules.Access.Contracts;
using Zaagl.SharedKernel.Security;

namespace Zaagl.Modules.Access;

public static class AccessModule
{
    /// <param name="requireConfiguredKeys">Production: <c>Access:GrantKeys</c> must be configured (ARC-004 §4.4,
    /// startup fails otherwise). Elsewhere an empty ring falls back to one random key for this run only, the same
    /// development shape as <c>ShortLinks:ScrambleKey</c> and <c>GUEST_KEY</c>.</param>
    public static IServiceCollection AddAccessModule(this IServiceCollection services, IConfiguration configuration, bool requireConfiguredKeys)
    {
        services.Configure<AccessOptions>(configuration.GetSection(AccessOptions.Section));
        var entries = configuration.GetSection(AccessOptions.Section).Get<AccessOptions>()?.GrantKeys ?? [];
        HmacKeyRing ring;
        if (entries.Count > 0)
        {
            ring = new HmacKeyRing(AccessGrantKeyRing.Domain, entries);
        }
        else if (requireConfiguredKeys)
        {
            throw new InvalidOperationException("Access:GrantKeys is not configured (ARC-004 §4.4).");
        }
        else
        {
            Console.Error.WriteLine("WARNING: Access:GrantKeys is not configured. Using a random key for this run only.");
            ring = HmacKeyRing.Ephemeral(AccessGrantKeyRing.Domain);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new AccessGrantKeyRing(ring));
        services.AddSingleton<AccessGate>();             // Phase 0 redirect gate (unchanged)
        services.AddSingleton<LinkKeys>();
        services.AddSingleton<ILinkKeys>(sp => sp.GetRequiredService<LinkKeys>());
        services.AddScoped<ContentAccessGate>();
        services.AddScoped<IAccessGate>(sp => sp.GetRequiredService<ContentAccessGate>());
        services.AddScoped<IAccessGrants>(sp => sp.GetRequiredService<ContentAccessGate>());
        return services;
    }
}
