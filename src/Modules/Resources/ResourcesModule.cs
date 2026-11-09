using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Modules.Resources.Application;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Resources;

/// <summary>BE-006 (ARC-006, C2): the resources schema, the registry and the quota ledger. No public endpoint yet.</summary>
public static class ResourcesModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", ResourcesDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddResourcesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<ResourcesOptions>(o =>
            o.QuarantineDays = configuration.GetValue("ShortLinks:QuarantineDays", o.QuarantineDays));
        services.AddDbContext<ResourcesDbContext>(o => Configure(o, connection));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ContentQuota>();
        services.AddScoped<IContentQuota>(sp => sp.GetRequiredService<ContentQuota>());
        services.AddScoped<IResourceRegistry, ResourceRegistry>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<ResourcesDbContext>("resources", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
