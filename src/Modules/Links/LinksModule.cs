using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Modules.Links;

public static class LinksModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", LinksDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddLinksModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<LinkOptions>(configuration.GetSection(LinkOptions.Section));
        services.Configure<WebRiskOptions>(configuration.GetSection(WebRiskOptions.Section));

        services.AddDbContext<LinksDbContext>(o => Configure(o, connection));
        services.AddHttpClient<IUrlReputationChecker, WebRiskChecker>(c => c.Timeout = TimeSpan.FromSeconds(3));

        services.AddSingleton<ShortCodeGenerator>();
        services.AddScoped<IValidator<LinkInput>, LinkInputValidator>();
        services.AddScoped<LinkService>();
        services.AddScoped<FolderService>();
        services.AddScoped<BulkLinkImporter>();
        services.AddScoped<LinkResolver>();
        services.AddScoped<ILinkResolver>(sp => sp.GetRequiredService<LinkResolver>());
        services.AddScoped<ILinkDirectory>(sp => sp.GetRequiredService<LinkResolver>());

        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<LinksDbContext>("links", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
