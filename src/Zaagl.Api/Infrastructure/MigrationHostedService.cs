using Zaagl.SharedKernel;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// Applies each module's EF Core migrations at startup when <c>Database:MigrateOnStartup</c> is true.
/// Retries for a while so the API can start together with a database that is still booting.
/// </summary>
public sealed class MigrationHostedService(
    IEnumerable<IModuleMigrator> migrators,
    IConfiguration configuration,
    ILogger<MigrationHostedService> logger) : IHostedService
{
    private const int MaxAttempts = 10;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Database:MigrateOnStartup")) return;

        foreach (var migrator in migrators)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await migrator.MigrateAsync(cancellationToken);
                    logger.LogInformation("Migrated {Module}", migrator.Module);
                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts && ex is not OperationCanceledException)
                {
                    logger.LogWarning("Migrating {Module} failed (attempt {Attempt}/{Max}): {Message}",
                        migrator.Module, attempt, MaxAttempts, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
