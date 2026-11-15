using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Modules.Collaboration.Application;
using Zaagl.Modules.Collaboration.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Collaboration;

/// <summary>Phase 2 E2.2/E2.3 (docs/phase2): the <c>collab</c> schema: before/after comparisons, pinned comments, approval gates,
/// drop-off portals.
/// Endpoints are mapped by the Api project (CollaborationEndpoints).</summary>
public static class CollaborationModule
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", CollabDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    /// <param name="workerOnly">The worker needs the schema and the kind handlers (Resources' purge calls them) only.</param>
    public static IServiceCollection AddCollaborationModule(this IServiceCollection services, IConfiguration configuration, bool workerOnly = false)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<CollabDbContext>(o => Configure(o, connection));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IResourceKindHandler, ComparisonKindHandler>();
        services.AddScoped<IResourceKindHandler, ApprovalKindHandler>();
        services.AddScoped<IResourceKindHandler, DropPortalKindHandler>();
        services.AddScoped<IResourceKindHandler, TableKindHandler>();
        services.AddScoped<IResourceKindHandler>(sp => new DropFilePurgeHandler(sp.GetRequiredService<CollabDbContext>(), ResourceKind.File));
        services.AddScoped<IResourceKindHandler>(sp => new DropFilePurgeHandler(sp.GetRequiredService<CollabDbContext>(), ResourceKind.Media));
        foreach (var kind in CommentService.Targets)
            services.AddScoped<IResourceKindHandler>(sp => new CommentPurgeHandler(sp.GetRequiredService<CollabDbContext>(), kind));
        if (workerOnly) return services;

        services.AddScoped<ImageParts>();
        services.AddScoped<ToolOwnership>();
        services.AddScoped<CompareService>();
        services.Configure<CommentOptions>(configuration.GetSection(CommentOptions.Section));
        services.AddScoped<CommentService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<DropPortalService>();
        services.AddScoped<TableService>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<CollabDbContext>("collab", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }
}
