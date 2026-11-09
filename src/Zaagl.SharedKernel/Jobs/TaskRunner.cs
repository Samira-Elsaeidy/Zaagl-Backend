using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zaagl.SharedKernel.Jobs;

/// <summary>
/// Handles one task kind of the task table owned by <typeparamref name="TContext"/>'s module. Implementations are
/// internal to that module (ARC-012 §6) and must be idempotent: delivery is at-least-once, so a handler re-reads
/// current state and does nothing if the effect already happened.
/// </summary>
public interface ITaskHandler<TContext> where TContext : DbContext
{
    string Kind { get; }

    Task HandleAsync(ClaimedTask task, CancellationToken cancellationToken);

    /// <summary>Delay before the next attempt after <paramref name="attempt"/> failed (default 1, 5, 15, 60 min).</summary>
    TimeSpan RetryDelay(int attempt) => TaskRetry.Default(attempt);

    /// <summary>Runs once when the task goes dead (attempts exhausted), e.g. scan → reject, never ready.</summary>
    Task OnDeadAsync(ClaimedTask task, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class TaskRetry
{
    private static readonly TimeSpan[] Schedule =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    public static TimeSpan Default(int attempt) => Schedule[Math.Clamp(attempt - 1, 0, Schedule.Length - 1)];
}

public sealed class TaskRunnerOptions
{
    /// <summary>Tasks claimed per poll.</summary>
    public int BatchSize { get; set; } = 10;
    /// <summary>Tasks of this queue running at once in this process.</summary>
    public int MaxConcurrency { get; set; } = 4;
    public int PollIntervalMs { get; set; } = 2000;
    /// <summary>Lease per claimed task; extended every lease/3 while the handler runs.</summary>
    public int LeaseSeconds { get; set; } = 300;
    /// <summary>Done rows older than this are deleted by the runner (ARC-012 §3 retention).</summary>
    public int DoneRetentionDays { get; set; } = 7;
    /// <summary>On shutdown, running handlers get this long to finish before they are cancelled; an unfinished
    /// task returns to the queue when its lease expires.</summary>
    public int ShutdownGraceSeconds { get; set; } = 55;
}

public static class TaskMetrics
{
    private static readonly Meter Meter = new("Zaagl.Jobs");
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("tasks_completed_total");
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("tasks_failed_total");
    public static readonly Counter<long> Dead = Meter.CreateCounter<long>("tasks_dead_total");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("task_duration", "ms");
}

/// <summary>
/// ARC-012 §6: claims due tasks of <typeparamref name="TContext"/>'s task table for the kinds that have a
/// registered <see cref="ITaskHandler{TContext}"/>, runs them with a concurrency cap, extends leases, records
/// done/failed/dead, and deletes old done rows. Safe with several worker replicas (SKIP LOCKED + leases).
/// </summary>
public sealed class TaskRunner<TContext>(
    IServiceScopeFactory scopes,
    TaskQueue queue,
    TaskRunnerOptions options,
    TimeProvider time,
    ILogger<TaskRunner<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly string _instance = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTime _nextCleanup = DateTime.MinValue;

    public string Instance => _instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string[] kinds;
        using (var scope = scopes.CreateScope())
            kinds = scope.ServiceProvider.GetServices<ITaskHandler<TContext>>().Select(h => h.Kind).Distinct().ToArray();
        if (kinds.Length == 0)
        {
            logger.LogInformation("Task runner for {Schema}.tasks has no handlers; idle.", queue.Schema);
            return;
        }
        logger.LogInformation("Task runner for {Schema}.tasks started for kinds {Kinds} as {Instance}.", queue.Schema, kinds, _instance);

        using var hardStop = new CancellationTokenSource();
        using var slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        var running = new List<Task>();
        var poll = TimeSpan.FromMilliseconds(options.PollIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            var claimedAny = false;
            try
            {
                var free = slots.CurrentCount;
                if (free > 0)
                {
                    var claimed = await ClaimAsync(kinds, Math.Min(free, options.BatchSize), stoppingToken);
                    claimedAny = claimed.Count > 0;
                    foreach (var task in claimed)
                    {
                        await slots.WaitAsync(stoppingToken);
                        running.Add(Task.Run(async () =>
                        {
                            try { await RunOneAsync(task, hardStop.Token); }
                            finally { slots.Release(); }
                        }, CancellationToken.None));
                    }
                }
                await CleanupIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database down or schema not migrated yet: keep the host alive and retry.
                logger.LogWarning(ex, "Task runner for {Schema}.tasks could not poll; retrying.", queue.Schema);
            }

            if (!claimedAny)
            {
                try { await Task.Delay(poll, time, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Graceful shutdown: stop claiming, let running handlers finish, then cancel them (their leases expire
        // and the tasks are claimed again later; no task relies on finishing).
        hardStop.CancelAfter(TimeSpan.FromSeconds(options.ShutdownGraceSeconds));
        try { await Task.WhenAll(running); } catch { /* each failure was already recorded */ }
    }

    private async Task<IReadOnlyList<ClaimedTask>> ClaimAsync(string[] kinds, int batch, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await queue.ClaimAsync(db, kinds, batch, TimeSpan.FromSeconds(options.LeaseSeconds), _instance,
            time.GetUtcNow().UtcDateTime, ct);
    }

    /// <summary>Runs one claimed task to done, retry or dead. Public for tests.</summary>
    public async Task RunOneAsync(ClaimedTask task, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var handler = scope.ServiceProvider.GetServices<ITaskHandler<TContext>>().First(h => h.Kind == task.Kind);
        var tags = new TagList { { "queue", queue.Schema }, { "kind", task.Kind } };

        if (task.Attempts > task.MaxAttempts)
        {
            // Re-claimed after a crash with no attempts left: never run it again.
            await FailAsync(db, handler, task, "attempts exhausted (lease expired while running)", tags);
            return;
        }

        var lease = TimeSpan.FromSeconds(options.LeaseSeconds);
        using var leaseLoop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var extender = ExtendLeaseLoopAsync(task, lease, leaseLoop.Token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await handler.HandleAsync(task, ct);
            leaseLoop.Cancel();
            await extender;
            if (await queue.CompleteAsync(db, task, _instance, time.GetUtcNow().UtcDateTime, CancellationToken.None))
                TaskMetrics.Completed.Add(1, tags);
            else
                logger.LogWarning("Task {Id} ({Kind}) finished after its lease was taken over; not completed twice.", task.Id, task.Kind);
        }
        catch (Exception ex)
        {
            leaseLoop.Cancel();
            await extender;
            await FailAsync(db, handler, task, $"{ex.GetType().Name}: {ex.Message}", tags);
            logger.LogWarning(ex, "Task {Id} ({Kind}) attempt {Attempt}/{Max} failed.", task.Id, task.Kind, task.Attempts, task.MaxAttempts);
        }
        finally
        {
            TaskMetrics.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        }
    }

    private async Task FailAsync(TContext db, ITaskHandler<TContext> handler, ClaimedTask task, string error, TagList tags)
    {
        var retryAt = time.GetUtcNow().UtcDateTime + handler.RetryDelay(task.Attempts);
        var status = await queue.FailAsync(db, task, _instance, error, retryAt, CancellationToken.None);
        TaskMetrics.Failed.Add(1, tags);
        if (status != QueuedTaskStatus.Dead) return;

        TaskMetrics.Dead.Add(1, tags);
        logger.LogError("Task {Id} ({Kind}, {DedupeKey}) is dead after {Attempts} attempts: {Error}",
            task.Id, task.Kind, task.DedupeKey, task.Attempts, error);
        try
        {
            await handler.OnDeadAsync(task, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dead-task handler for {Id} ({Kind}) failed.", task.Id, task.Kind);
        }
    }

    /// <summary>Extends the lease every lease/3 until the handler ends. A transient error (database blip) is logged
    /// and retried at the next tick instead of ending the loop (Head round 3 note 4): with two more ticks before the
    /// lease runs out, one failed extension no longer lets the task be re-claimed while it still runs. The loop stops
    /// only when cancelled or when the claim is no longer held (re-claimed or finished).</summary>
    public async Task ExtendLeaseLoopAsync(ClaimedTask task, TimeSpan lease, CancellationToken ct)
    {
        var interval = lease / 3;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                if (!await queue.ExtendLeaseAsync(db, task, _instance, time.GetUtcNow().UtcDateTime + lease, ct))
                {
                    logger.LogWarning("Task {Id} ({Kind}) lost its lease (re-claimed or finished); not extending.", task.Id, task.Kind);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not extend the lease of task {Id}; retrying at the next tick.", task.Id);
            }
        }
    }

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now < _nextCleanup) return;
        _nextCleanup = now.AddHours(24);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var deleted = await queue.DeleteDoneAsync(db, now.AddDays(-options.DoneRetentionDays), ct);
        if (deleted > 0) logger.LogInformation("Deleted {Count} done tasks from {Schema}.tasks.", deleted, queue.Schema);
    }
}
