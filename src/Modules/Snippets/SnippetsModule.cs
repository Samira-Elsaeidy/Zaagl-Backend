using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Application;
using Zaagl.Modules.Snippets.Contracts;
using Zaagl.Modules.Snippets.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Snippets;

/// <summary>BE-008 (ARC-010): the snippets schema, immutable versions, create/edit and the read side. Endpoints are
/// mapped by the Api project (SnippetsEndpoints, PublicContentEndpoints, DeliveryEndpoints).</summary>
public static class SnippetsModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", SnippetsDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddSnippetsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<SnippetOptions>(configuration.GetSection(SnippetOptions.Section));
        services.AddDbContext<SnippetsDbContext>(o => Configure(o, connection));
        services.AddScoped<SnippetService>();
        services.AddScoped<SnippetReader>();
        services.AddScoped<ISnippetReader>(sp => sp.GetRequiredService<SnippetReader>());
        services.AddScoped<IResourceKindHandler>(sp => sp.GetRequiredService<SnippetReader>());
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<SnippetsDbContext>("snippets", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
