using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zaagl.Modules.Files;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Links.Tests;

/// <summary>
/// BE-013 / ARC-012 §3: the files.tasks queue on the real local PostgreSQL. Each test uses its own task kinds, so
/// tests (and any worker) never claim each other's rows. The files migration is applied here (expand-only; the
/// API's MigrationHostedService applies the same one).
/// </summary>
public sealed class TaskQueueTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim MigrateOnce = new(1, 1);
    private static bool _migrated;

    private readonly ServiceProvider _services;
    private readonly TaskQueue _queue = new(FilesDbContext.Schema);
    private readonly string _kind = "test-" + Guid.NewGuid().ToString("N")[..12];

    public TaskQueueTests()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<FilesDbContext>(o => FilesModule.Configure(o, RealApiFactory.ConnectionString));
        _services = sc.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        await MigrateOnce.WaitAsync();
        try
        {
            if (_migrated) return;
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FilesDbContext>().Database.MigrateAsync();
            _migrated = true;
        }
        finally { MigrateOnce.Release(); }
    }

    public async Task DisposeAsync()
    {
        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<FilesDbContext>().Tasks.Where(t => t.Kind.StartsWith(_kind)).ExecuteDeleteAsync();
        await _services.DisposeAsync();
    }

    private static DateTime Now => TimeProvider.System.GetUtcNow().UtcDateTime;

    private FilesDbContext NewDb() => _services.CreateScope().ServiceProvider.GetRequiredService<FilesDbContext>();

    private async Task<long?> EnqueueAsync(string dedupe, int maxAttempts = 3, string? kind = null)
    {
        await using var db = NewDb();
        return await _queue.EnqueueAsync(db, kind ?? _kind, dedupe, "{\"resourceId\":\"x\"}", maxAttempts, Now, Now, default);
    }

    private async Task<IReadOnlyList<ClaimedTask>> ClaimAsync(string instance, int batch = 10, TimeSpan? lease = null, DateTime? now = null)
    {
        await using var db = NewDb();
        return await _queue.ClaimAsync(db, [_kind], batch, lease ?? TimeSpan.FromMinutes(5), instance, now ?? Now, default);
    }

    [Fact]
    public async Task An_open_task_with_the_same_dedupe_key_is_not_enqueued_twice_but_a_finished_one_can_be()
    {
        var key = _kind + ":a";
        Assert.NotNull(await EnqueueAsync(key));
        Assert.Null(await EnqueueAsync(key));

        var claimed = Assert.Single(await ClaimAsync("w1"));
        Assert.Null(await EnqueueAsync(key)); // running is still open
        await using (var db = NewDb()) Assert.True(await _queue.CompleteAsync(db, claimed, "w1", Now, default));
        Assert.NotNull(await EnqueueAsync(key));
    }

    [Fact]
    public async Task Enqueue_commits_and_rolls_back_with_the_callers_transaction()
    {
        await using (var db = NewDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.NotNull(await _queue.EnqueueAsync(db, _kind, _kind + ":rolled-back", "{}", 3, Now, Now, default));
            await tx.RollbackAsync();
        }
        await using (var db = NewDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.NotNull(await _queue.EnqueueAsync(db, _kind, _kind + ":committed", "{}", 3, Now, Now, default));
            await tx.CommitAsync();
        }
        var claimed = await ClaimAsync("w1");
        Assert.Equal([_kind + ":committed"], claimed.Select(c => c.DedupeKey));
    }

    [Fact]
    public async Task Concurrent_claimers_never_share_a_task()
    {
        for (var i = 0; i < 60; i++) await EnqueueAsync($"{_kind}:{i}");

        var batches = await Task.WhenAll(Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
        {
            var mine = new List<long>();
            while (true)
            {
                var got = await ClaimAsync($"w{w}", batch: 4);
                if (got.Count == 0) return mine;
                mine.AddRange(got.Select(t => t.Id));
            }
        })));

        var all = batches.SelectMany(b => b).ToList();
        Assert.Equal(60, all.Count);
        Assert.Equal(60, all.Distinct().Count());
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_and_the_old_holder_cannot_complete_it()
    {
        await EnqueueAsync(_kind + ":lease");
        var first = Assert.Single(await ClaimAsync("old", lease: TimeSpan.FromSeconds(1)));
        Assert.Empty(await ClaimAsync("new")); // still leased

        var later = Now.AddSeconds(5);
        var again = Assert.Single(await ClaimAsync("new", now: later));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(2, again.Attempts);

        await using var db = NewDb();
        Assert.False(await _queue.CompleteAsync(db, first, "old", Now, default));
        Assert.True(await _queue.CompleteAsync(db, again, "new", Now, default));
    }

    [Fact]
    public async Task A_stale_run_of_the_same_instance_cannot_complete_fail_or_extend_the_new_claim()
    {
        // Head round 3 note 4: the same process re-claims the task after its own lease expired; both claims carry
        // locked_by = w1, so only the attempt number tells them apart.
        await EnqueueAsync(_kind + ":fence");
        var stale = Assert.Single(await ClaimAsync("w1", lease: TimeSpan.FromSeconds(1)));
        var current = Assert.Single(await ClaimAsync("w1", now: Now.AddSeconds(5)));
        Assert.Equal(stale.Id, current.Id);
        Assert.Equal(stale.Attempts + 1, current.Attempts);

        await using var db = NewDb();
        Assert.False(await _queue.ExtendLeaseAsync(db, stale, "w1", Now.AddMinutes(10), default));
        Assert.Null(await _queue.FailAsync(db, stale, "w1", "late failure", Now, default));
        Assert.False(await _queue.CompleteAsync(db, stale, "w1", Now, default));
        Assert.Equal(QueuedTaskStatus.Running, (await RowAsync(current.Id)).Status);

        Assert.True(await _queue.ExtendLeaseAsync(db, current, "w1", Now.AddMinutes(10), default));
        Assert.True(await _queue.CompleteAsync(db, current, "w1", Now, default));
        Assert.Equal(QueuedTaskStatus.Done, (await RowAsync(current.Id)).Status);
    }

    [Fact]
    public async Task The_lease_extender_keeps_going_after_a_transient_error()
    {
        var handler = new FlakyHandler(_kind, failTimes: 0);
        var resolutions = 0;
        var sc = new ServiceCollection();
        sc.AddLogging();
        var options = new DbContextOptionsBuilder<FilesDbContext>();
        FilesModule.Configure(options, RealApiFactory.ConnectionString);
        // The first database the extender asks for fails (a blip); later ones work.
        sc.AddScoped(_ => Interlocked.Increment(ref resolutions) == 1
            ? throw new InvalidOperationException("database blip")
            : new FilesDbContext(options.Options));
        sc.AddSingleton<ITaskHandler<FilesDbContext>>(handler);
        await using var sp = sc.BuildServiceProvider();
        var runner = Runner(handler, sp);

        await EnqueueAsync(_kind + ":extend");
        var lease = TimeSpan.FromMilliseconds(600);
        ClaimedTask claimed;
        await using (var db = NewDb())
            claimed = Assert.Single(await _queue.ClaimAsync(db, [_kind], 1, lease, runner.Instance, Now, default));
        var firstLease = (await RowAsync(claimed.Id)).LockedUntil!.Value;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1300));
        await runner.ExtendLeaseLoopAsync(claimed, lease, cts.Token);

        Assert.True(resolutions >= 3, $"only {resolutions} extension attempts"); // kept trying after the failure
        Assert.True((await RowAsync(claimed.Id)).LockedUntil > firstLease);
    }

    [Fact]
    public async Task Not_due_tasks_are_not_claimed()
    {
        await using (var db = NewDb())
            await _queue.EnqueueAsync(db, _kind, _kind + ":later", "{}", 3, Now.AddMinutes(10), Now, default);
        Assert.Empty(await ClaimAsync("w1"));
        Assert.Single(await ClaimAsync("w1", now: Now.AddMinutes(11)));
    }

    private sealed class FlakyHandler(string kind, int failTimes) : ITaskHandler<FilesDbContext>
    {
        public int Calls;
        public int DeadCalls;
        public string Kind => kind;

        public Task HandleAsync(ClaimedTask task, CancellationToken cancellationToken) =>
            ++Calls <= failTimes ? throw new InvalidOperationException("boom") : Task.CompletedTask;

        public TimeSpan RetryDelay(int attempt) => TimeSpan.Zero;

        public Task OnDeadAsync(ClaimedTask task, CancellationToken cancellationToken)
        {
            DeadCalls++;
            return Task.CompletedTask;
        }
    }

    private TaskRunner<FilesDbContext> Runner(FlakyHandler handler, ServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), _queue,
            new TaskRunnerOptions { PollIntervalMs = 50, BatchSize = 5, MaxConcurrency = 2 },
            TimeProvider.System, NullLogger<TaskRunner<FilesDbContext>>.Instance);

    private ServiceProvider ProviderWith(FlakyHandler handler)
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<FilesDbContext>(o => FilesModule.Configure(o, RealApiFactory.ConnectionString));
        sc.AddSingleton<ITaskHandler<FilesDbContext>>(handler);
        return sc.BuildServiceProvider();
    }

    private async Task<QueuedTask> RowAsync(long id)
    {
        await using var db = NewDb();
        return await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    [Fact]
    public async Task A_failing_task_is_retried_then_goes_dead_and_runs_its_dead_handler_once()
    {
        var handler = new FlakyHandler(_kind, failTimes: 99);
        await using var sp = ProviderWith(handler);
        var runner = Runner(handler, sp);
        var id = (await EnqueueAsync(_kind + ":dead", maxAttempts: 3))!.Value;

        for (var i = 0; i < 3; i++)
        {
            await using var db = NewDb();
            var claimed = Assert.Single(await _queue.ClaimAsync(db, [_kind], 1, TimeSpan.FromMinutes(5), runner.Instance, Now.AddSeconds(1), default));
            await runner.RunOneAsync(claimed, default);
        }

        var row = await RowAsync(id);
        Assert.Equal(QueuedTaskStatus.Dead, row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Contains("boom", row.LastError);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(1, handler.DeadCalls);
        Assert.Empty(await ClaimAsync("w1", now: Now.AddHours(1))); // dead is never claimed again
    }

    [Fact]
    public async Task The_runner_claims_and_completes_tasks_in_the_background()
    {
        var handler = new FlakyHandler(_kind, failTimes: 1); // first attempt of the first task fails, then succeeds
        await using var sp = ProviderWith(handler);
        var runner = Runner(handler, sp);
        var ids = new List<long>();
        for (var i = 0; i < 4; i++) ids.Add((await EnqueueAsync($"{_kind}:bg{i}"))!.Value);

        using var cts = new CancellationTokenSource();
        await runner.StartAsync(cts.Token);
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(15))
            {
                var statuses = await Task.WhenAll(ids.Select(RowAsync));
                if (statuses.All(r => r.Status == QueuedTaskStatus.Done)) break;
                await Task.Delay(100);
            }
            var rows = await Task.WhenAll(ids.Select(RowAsync));
            Assert.All(rows, r => Assert.Equal(QueuedTaskStatus.Done, r.Status));
            Assert.Equal(5, handler.Calls);
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }
}
