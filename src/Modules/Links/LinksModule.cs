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

        // Coordinator leases blocks of numbers (which length grows over time, spec §4.2); CodeGenerator
        // (a 10-round keyed Feistel permutation, spec §5) hides their order and encodes them in base 62.
        // The scramble key must never change once codes have been issued for a length (spec §5.3).
        var scrambleKey = configuration["ShortLinks:ScrambleKey"];
        if (string.IsNullOrWhiteSpace(scrambleKey))
        {
            // Dev-only fallback so the app still boots locally. Every restart reshuffles the number
            // space, so already-issued codes may stop decoding correctly - fine for a throwaway dev
            // database, never acceptable once real links exist.
            scrambleKey = Guid.NewGuid().ToString("N");
            Console.Error.WriteLine(
                "WARNING: ShortLinks:ScrambleKey is not configured. Using a random key for this run only. " +
                "Set ShortLinks:ScrambleKey (dotnet user-secrets or an environment variable) before issuing real links.");
        }
        services.AddSingleton<ICodePermutation>(new FeistelPermutation(scrambleKey));
        services.AddSingleton<CodeGenerator>();

        // CoordinatorLeaseClient is a singleton (it holds the current leased block), but its HttpClient
        // still comes from IHttpClientFactory for pooled connections and DNS refresh.
        var coordinatorUrl = configuration["Coordinator:BaseUrl"] ?? "http://localhost:5085";
        services.AddHttpClient(nameof(CoordinatorLeaseClient), c => c.BaseAddress = new Uri(coordinatorUrl));
        services.AddSingleton(sp =>
            new CoordinatorLeaseClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CoordinatorLeaseClient))));
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
