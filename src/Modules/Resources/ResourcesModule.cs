using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Application;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Resources.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;
using Zaagl.SharedKernel.Security;

namespace Zaagl.Modules.Resources;

/// <summary>BE-006 (ARC-006, C2): the resources schema, the registry and the quota ledger. BE-008 adds the raw and
/// owner ticket slice of <see cref="IDownloadGrants"/>. No public endpoint of its own yet.</summary>
public static class ResourcesModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", ResourcesDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    /// <param name="requireConfiguredKeys">Production: <c>Content:TicketKeys</c> must be configured (ARC-008 §3, startup
    /// fails otherwise). Elsewhere an empty ring falls back to one random key for this run only.</param>
    /// <param name="workerOnly">The worker (ARC-012 §6) needs only the registry and the quota ledger: no tickets, no owner routes
    /// (those need the Links snapshots, which the worker does not host).</param>
    public static IServiceCollection AddResourcesModule(
        this IServiceCollection services, IConfiguration configuration, bool requireConfiguredKeys = false, bool workerOnly = false)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<ResourcesOptions>(o =>
        {
            o.QuarantineDays = configuration.GetValue("ShortLinks:QuarantineDays", o.QuarantineDays);
            o.EvidenceRetentionDays = configuration.GetValue("Abuse:EvidenceRetentionDays", o.EvidenceRetentionDays);
        });
        services.Configure<BurnOptions>(configuration.GetSection(BurnOptions.Section));
        if (workerOnly)
        {
            services.AddDbContext<ResourcesDbContext>(o => Configure(o, connection));
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<ContentQuota>();
            services.AddScoped<IContentQuota>(sp => sp.GetRequiredService<ContentQuota>());
            services.AddScoped<IResourceRegistry, ResourceRegistry>();
            services.AddScoped<ResourceLifecycle>();
            return services;
        }
        services.Configure<ContentOptions>(configuration.GetSection(ContentOptions.Section));
        var entries = configuration.GetSection(ContentOptions.Section).Get<ContentOptions>()?.TicketKeys ?? [];
        HmacKeyRing ring;
        if (entries.Count > 0)
        {
            ring = new HmacKeyRing(TicketKeyRing.Domain, entries);
        }
        else if (requireConfiguredKeys)
        {
            throw new InvalidOperationException("Content:TicketKeys is not configured (ARC-008 §3).");
        }
        else
        {
            Console.Error.WriteLine("WARNING: Content:TicketKeys is not configured. Using a random key for this run only.");
            ring = HmacKeyRing.Ephemeral(TicketKeyRing.Domain);
        }
        services.AddSingleton(new TicketKeyRing(ring));

        services.AddDbContext<ResourcesDbContext>(o => Configure(o, connection));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ContentQuota>();
        services.AddScoped<IContentQuota>(sp => sp.GetRequiredService<ContentQuota>());
        services.AddScoped<IResourceRegistry, ResourceRegistry>();
        services.AddScoped<IDownloadGrants, DownloadGrants>();
        services.AddScoped<ResourceLifecycle>();
        services.AddScoped<ILinkLifecycleHandler>(sp => sp.GetRequiredService<ResourceLifecycle>());
        services.AddScoped<OwnerResources>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<ResourcesDbContext>("resources", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

    /// <summary>Worker only (ARC-012 section 4): the lifecycle sweeps (burn finaliser, purge and guest expiry, ended holds, orphans, grant
    /// cleanup, tombstones) and the daily quota reconciliation. Needs <see cref="IResourceBytes"/> (Files) in the same process.</summary>
    public static IServiceCollection AddResourceJobs(this IServiceCollection services)
    {
        services.AddSingleton<ResourceMaintenanceJob>();
        services.AddHostedService(sp => sp.GetRequiredService<ResourceMaintenanceJob>());
        return services;
    }
}
