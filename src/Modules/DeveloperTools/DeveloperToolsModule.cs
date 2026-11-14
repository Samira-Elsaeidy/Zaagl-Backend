using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Modules.DeveloperTools.Application;
using Zaagl.Modules.DeveloperTools.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.DeveloperTools;

/// <summary>Phase 2 E2.1 (docs/phase2): the <c>devtools</c> schema: vault secrets, webhook catchers, bug capsules. Endpoints are
/// mapped by the Api project (DeveloperToolsEndpoints).</summary>
public static class DeveloperToolsModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", DevToolsDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    /// <param name="workerOnly">The worker needs the schema, the kind handlers (Resources' purge calls them) and the sweeps; no
    /// request-path services (those need the Links snapshots, which the worker does not host).</param>
    public static IServiceCollection AddDeveloperToolsModule(this IServiceCollection services, IConfiguration configuration, bool workerOnly = false)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<DevToolsDbContext>(o => Configure(o, connection));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SecretLifecycle>();
        services.AddScoped<IResourceKindHandler>(sp => sp.GetRequiredService<SecretLifecycle>());
        services.AddScoped<IDevToolsSweep, VaultExpirySweep>();
        services.AddSingleton<DevToolsSweeper>();
        if (workerOnly) return services;

        services.AddScoped<ILinkLifecycleHandler>(sp => sp.GetRequiredService<SecretLifecycle>());
        services.AddScoped<VaultService>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<DevToolsDbContext>("devtools", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

    /// <summary>Worker only: the DeveloperTools sweeps every minute.</summary>
    public static IServiceCollection AddDeveloperToolsJobs(this IServiceCollection services)
    {
        services.AddHostedService<DevToolsMaintenanceJob>();
        return services;
    }
}
