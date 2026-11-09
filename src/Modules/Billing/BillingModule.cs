using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Modules.Billing.Application;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Billing.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Billing;

public static class BillingModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", BillingDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<StripeOptions>(o =>
        {
            configuration.GetSection(StripeOptions.Section).Bind(o);
            if (string.IsNullOrWhiteSpace(o.ReturnBaseUrl) || o.ReturnBaseUrl == "http://localhost:3000")
                o.ReturnBaseUrl = configuration["Frontend:BaseUrl"] ?? o.ReturnBaseUrl;
        });
        services.AddDbContext<BillingDbContext>(o => Configure(o, connection));
        services.AddScoped<IPlanProvider, PlanProvider>();

        // BE-003 (ARC-005, C5): plan values from configuration, validated at startup.
        services.AddOptions<PlansOptions>().Bind(configuration.GetSection(PlansOptions.Section)).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<PlansOptions>, PlansOptionsValidator>();
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PlanCatalog>();
        services.AddScoped<IContentPlanProvider, ContentPlanProvider>();
        services.AddScoped<BillingService>();
        // PD-58: plan-change delivery (API-key revocation) with a pending marker and a periodic retry.
        services.AddScoped<PlanChangeOutbox>();
        services.AddHostedService<PlanChangeRetryJob>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<BillingDbContext>("billing", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
