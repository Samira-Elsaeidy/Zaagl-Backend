using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Jobs;
using Zaagl.SharedKernel.Persistence;

namespace Zaagl.Modules.Files;

public static class FilesModule
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations", FilesDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    /// <summary>Services shared by the API and the worker: object storage (BE-004, C7 on local disk, PD-15), the
    /// files schema and its task queue (BE-013). Registers no hosted service; the worker adds the runner with
    /// <see cref="AddFilesTaskRunner"/>. Upload sessions and finalize arrive with BE-011/BE-012.</summary>
    public static IServiceCollection AddFilesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.AddSingleton<LocalObjectStorage>();
        services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<LocalObjectStorage>());

        services.AddDbContext<FilesDbContext>(o => Configure(o, connection));
        services.AddKeyedSingleton(FilesDbContext.Schema, new TaskQueue(FilesDbContext.Schema));
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<FilesDbContext>("files", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

    /// <summary>Worker only (ARC-012 §6): the runner for <c>files.tasks</c>. Handlers register as
    /// <c>ITaskHandler&lt;FilesDbContext&gt;</c> (none yet; scan/promote/delete-bytes come with BE-011/BE-012).</summary>
    public static IServiceCollection AddFilesTaskRunner(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Jobs:Files").Get<TaskRunnerOptions>() ?? new TaskRunnerOptions();
        services.AddHostedService(sp => new TaskRunner<FilesDbContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredKeyedService<TaskQueue>(FilesDbContext.Schema),
            options,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TaskRunner<FilesDbContext>>>()));
        return services;
    }
}
