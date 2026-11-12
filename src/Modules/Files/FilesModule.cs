using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Files.Application;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
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

    /// <summary>Services shared by the API and the worker: object storage (C7 on local disk, PD-15), the files schema and its
    /// task queue (BE-013), the stored-object reads, the hash blocklist. Registers no hosted service: the worker adds the
    /// runner with <see cref="AddFilesTaskRunner"/>. The tus session service, which needs Links and Billing, is
    /// <see cref="AddFilesUploads"/> (API only).</summary>
    public static IServiceCollection AddFilesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.Section));
        services.Configure<ScanOptions>(configuration.GetSection(ScanOptions.Section));
        services.Configure<ClamAvOptions>(configuration.GetSection(ClamAvOptions.Section));
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.Section));
        services.AddSingleton<LocalObjectStorage>();
        services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<LocalObjectStorage>());
        services.AddSingleton<TempUploadFiles>();
        services.TryAddSingleton<IDiskSpaceProbe, DriveDiskSpaceProbe>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<FilesDbContext>(o => Configure(o, connection));
        services.AddKeyedSingleton(FilesDbContext.Schema, new TaskQueue(FilesDbContext.Schema));
        services.AddScoped<StoredObjects>();
        services.AddScoped<IStoredObjects>(sp => sp.GetRequiredService<StoredObjects>());
        services.AddScoped<IStoredObjectSummaries>(sp => sp.GetRequiredService<StoredObjects>());
        services.AddScoped<IMediaMetadata, MediaMetadata>();
        services.AddScoped<IResourceBytes>(sp => sp.GetRequiredService<StoredObjects>());
        services.AddScoped<IUploadStats>(sp => sp.GetRequiredService<StoredObjects>());
        services.AddScoped<IContentHashBlocklist, ContentHashBlocklist>();
        services.AddSingleton<IModuleMigrator>(sp =>
            new ModuleMigrator<FilesDbContext>("files", sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

    /// <summary>API only: tus create, offset, append, abort and <c>/complete</c> (ARC-009). Needs Links (the content link),
    /// Billing (the plan) and Resources (the registry and the quota) in the same process.</summary>
    public static IServiceCollection AddFilesUploads(this IServiceCollection services)
    {
        services.AddScoped<DiskAdmission>();
        services.AddScoped<UploadService>();
        services.AddScoped<UploadSessionExpiry>();
        return services;
    }

    /// <summary>The scan task's services: the scanner (clamd, or the fake in Development only), the concurrency gate and the
    /// handlers. The Api never runs them in production; tests host them in-process.</summary>
    /// <param name="isDevelopment">The fake scanner is refused anywhere else (ARC-009 section 10): startup fails.</param>
    public static IServiceCollection AddFilesTaskHandlers(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        var provider = configuration.GetSection(ScanOptions.Section)["Provider"] ?? "Clamd";
        if (provider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
        {
            if (!isDevelopment)
                throw new InvalidOperationException("Scan:Provider=Fake is allowed only in the Development environment (fail-closed scanning).");
            services.AddSingleton<IMalwareScanner, FakeScanner>();
        }
        else if (provider.Equals("Clamd", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IMalwareScanner, ClamdScanner>();
        }
        else
        {
            throw new InvalidOperationException($"Unknown Scan:Provider '{provider}' (use Clamd or Fake).");
        }

        services.AddSingleton<ScanConcurrencyGate>();
        services.AddScoped<ITaskHandler<FilesDbContext>, ScanTaskHandler>();
        services.AddScoped<ITaskHandler<FilesDbContext>, DeleteBytesTaskHandler>();
        services.AddSingleton<MediaConcurrencyGate>();
        services.AddScoped<ITaskHandler<FilesDbContext>, MediaPreviewTaskHandler>();
        services.AddScoped<ScanTaskHandler>();
        return services;
    }

    /// <summary>Worker only (ARC-012 section 6): the runner for <c>files.tasks</c> with its handlers, and the idle-session
    /// expiry job.</summary>
    public static IServiceCollection AddFilesTaskRunner(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false)
    {
        services.AddFilesTaskHandlers(configuration, isDevelopment);
        services.AddScoped<UploadSessionExpiry>();
        services.AddHostedService<UploadSessionExpiryJob>();
        services.AddHostedService<MediaBackfillJob>();

        var options = configuration.GetSection("Jobs:Files").Get<TaskRunnerOptions>() ?? new TaskRunnerOptions();
        services.AddHostedService(sp => new TaskRunner<FilesDbContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredKeyedService<TaskQueue>(FilesDbContext.Schema),
            options,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<TaskRunner<FilesDbContext>>>()));
        return services;
    }
}
