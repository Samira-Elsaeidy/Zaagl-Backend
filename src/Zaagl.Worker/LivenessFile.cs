using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Worker;

/// <summary>
/// ARC-012 §6 health without a public endpoint: while the database answers, touches <c>Worker:LivenessFile</c>
/// (default <c>/tmp/zaagl-worker-alive</c>) every <c>Worker:LivenessSeconds</c> (default 15). A Docker healthcheck
/// fails when the file is older than a few intervals (e.g. <c>find /tmp/zaagl-worker-alive -mmin -1</c>).
/// </summary>
public sealed class LivenessFile(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<LivenessFile> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = configuration["Worker:LivenessFile"] is { Length: > 0 } p
            ? p
            : Path.Combine(Path.GetTempPath(), "zaagl-worker-alive");
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Worker:LivenessSeconds", 15));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
                if (await db.Database.CanConnectAsync(stoppingToken))
                    await File.WriteAllTextAsync(path, time.GetUtcNow().ToString("O"), stoppingToken);
                else
                    logger.LogWarning("Worker liveness: database not reachable.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker liveness check failed.");
            }

            try { await Task.Delay(interval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
