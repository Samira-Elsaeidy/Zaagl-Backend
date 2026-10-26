using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Analytics;

public static class AnalyticsModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", AnalyticsDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<AnalyticsDbContext>(o => Configure(o, connection));
        services.AddSingleton<ClickChannel>();
        services.AddSingleton<RawEventBudget>();
        services.AddSingleton<IClickRecorder, ClickRecorder>();
        services.AddScoped<IAnalyticsQueries, AnalyticsQueries>();
        services.AddScoped<IArchiveQueries, ArchiveQueries>();
        services.AddScoped<IArchiveWriter, ArchiveWriter>();
        services.AddHostedService<ClickWriterService>();
        services.AddHostedService<PartitionMaintenanceService>();

        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<AnalyticsDbContext>("analytics", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
