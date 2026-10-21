using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Identity;

public static class IdentityModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", IdentityDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.AddDbContext<IdentityDbContext>(o => Configure(o, connection));
        services.AddSingleton<TokenService>();
        services.AddScoped<IdentityService>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<IdentityDbContext>("identity", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
