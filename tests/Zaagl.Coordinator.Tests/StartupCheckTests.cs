using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zaagl.Coordinator;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;
using Zaagl.SharedKernel.Testing;

namespace Zaagl.Coordinator.Tests;

/// <summary>
/// Integration tests against the real local dev Postgres instance (not Testcontainers - none is set up
/// in this solution yet, and introducing it was out of scope for this phase). Each test wipes and
/// re-seeds the <c>alloc</c> schema, so these must not run concurrently with a manually-running
/// coordinator or with each other (xUnit runs a class's tests sequentially by default, which is
/// sufficient here since there is exactly one test class).
/// </summary>
public sealed class StartupCheckTests : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _tempDir;
    private readonly HwmStore _hwmStore;
    private readonly IAllocatorSink _sink;

    public StartupCheckTests()
    {
        var connectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _tempDir = Path.Combine(Path.GetTempPath(), "zaagl-coordinator-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _hwmStore = new HwmStore(Path.Combine(_tempDir, "hwm.json"));
        _sink = new LocalFileSystemAllocatorSink(Path.Combine(_tempDir, "sink"));

        AllocSchema.EnsureCreatedAsync(_dataSource, CancellationToken.None).GetAwaiter().GetResult();
        Wipe();
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void Wipe()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE alloc.leases, alloc.reservations, alloc.recovery_log, alloc.installation RESTART IDENTITY; " +
            "UPDATE alloc.pools SET current_length = 5, next_offset = 0, restore_detected_at = NULL WHERE pool_id = 'default';";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Row1_fresh_database_and_empty_sink_is_not_initialised()
    {
        var result = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);

        Assert.Equal("1", result.Row);
        Assert.Equal(LeasingState.NotInitialised, result.State);
    }

    [Fact]
    public async Task Row4_database_equals_hwm_is_ok()
    {
        var installationId = Guid.NewGuid();
        const string fingerprint = "test-fingerprint";
        await InsertInstallationAsync(installationId, fingerprint);
        await _hwmStore.WriteAsync(new HwmFile(1, installationId.ToString(), fingerprint, "active",
            new Dictionary<string, PoolHwm> { ["default"] = new(5, 0, DateTimeOffset.UtcNow) }), CancellationToken.None);

        var result = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);

        Assert.Equal("4", result.Row);
        Assert.Equal(LeasingState.Ok, result.State);
    }

    [Fact]
    public async Task Row3_database_behind_hwm_catches_up_and_stays_ok()
    {
        var installationId = Guid.NewGuid();
        const string fingerprint = "test-fingerprint";
        await InsertInstallationAsync(installationId, fingerprint);
        await _hwmStore.WriteAsync(new HwmFile(1, installationId.ToString(), fingerprint, "active",
            new Dictionary<string, PoolHwm> { ["default"] = new(5, 5000, DateTimeOffset.UtcNow) }), CancellationToken.None);
        // Database position (0) left behind the HWM (5000), as if the database were restored.

        var result = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);

        Assert.Equal("3", result.Row);
        Assert.Equal(LeasingState.Ok, result.State);
        Assert.NotNull(result.RestoreDetectedAt);

        await using var conn = await _dataSource.OpenConnectionAsync();
        var (length, offset) = await StartupCheck.ReadPoolPositionAsync(conn, "default", CancellationToken.None);
        Assert.Equal(5, length);
        Assert.Equal(5000, offset); // caught up to the HWM
    }

    [Fact]
    public async Task Row5_database_ahead_of_hwm_is_blocked()
    {
        var installationId = Guid.NewGuid();
        const string fingerprint = "test-fingerprint";
        await InsertInstallationAsync(installationId, fingerprint);
        await SetPoolOffsetAsync(5, 9000);
        await _hwmStore.WriteAsync(new HwmFile(1, installationId.ToString(), fingerprint, "active",
            new Dictionary<string, PoolHwm> { ["default"] = new(5, 100, DateTimeOffset.UtcNow) }), CancellationToken.None);

        var result = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);

        Assert.Equal("5", result.Row);
        Assert.Equal(LeasingState.Blocked, result.State);
        Assert.Equal("hwm-behind-database", result.Reason);
    }

    [Fact]
    public async Task Row6_hwm_active_but_installation_missing_is_blocked()
    {
        await _hwmStore.WriteAsync(new HwmFile(1, Guid.NewGuid().ToString(), "fp", "active",
            new Dictionary<string, PoolHwm> { ["default"] = new(5, 0, DateTimeOffset.UtcNow) }), CancellationToken.None);
        // No installation row inserted - simulates a database restored to before init.

        var result = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);

        Assert.Equal("6", result.Row);
        Assert.Equal(LeasingState.Blocked, result.State);
    }

    [Fact]
    public async Task TrustDatabase_recovers_from_row5_and_leasing_resumes()
    {
        var installationId = Guid.NewGuid();
        const string fingerprint = "test-fingerprint";
        await InsertInstallationAsync(installationId, fingerprint);
        await SetPoolOffsetAsync(5, 9000);
        await _hwmStore.WriteAsync(new HwmFile(1, installationId.ToString(), fingerprint, "active",
            new Dictionary<string, PoolHwm> { ["default"] = new(5, 100, DateTimeOffset.UtcNow) }), CancellationToken.None);

        var blocked = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);
        Assert.Equal(LeasingState.Blocked, blocked.State);

        var recovery = new RecoveryService(_dataSource, _hwmStore, _sink);
        var outcome = await recovery.TrustDatabaseAsync("test-operator", confirmDatabaseComplete: true, CancellationToken.None);
        Assert.True(outcome.Success);

        var after = await StartupCheck.EvaluateAsync(_dataSource, _hwmStore, _sink, CancellationToken.None);
        Assert.Equal(LeasingState.Ok, after.State);
    }

    private async Task InsertInstallationAsync(Guid installationId, string fingerprint)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO alloc.installation (installation_id, key_fingerprint, initialized_at) VALUES ($1, $2, now())", conn);
        cmd.Parameters.AddWithValue(installationId);
        cmd.Parameters.AddWithValue(fingerprint);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SetPoolOffsetAsync(int length, long offset)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE alloc.pools SET current_length = $1, next_offset = $2 WHERE pool_id = 'default'", conn);
        cmd.Parameters.AddWithValue((short)length);
        cmd.Parameters.AddWithValue(offset);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- Failpoint tests (spec §8.3): host the real coordinator app (via WebApplicationFactory against
    // Program's own minimal-API lease handler, not a reimplementation), crash it exactly at a named point,
    // then start a fresh host against the same HWM file/sink/database to prove the restart self-heals.

    private sealed class ThrowAtFailpoint(string targetName) : IFailpoints
    {
        public bool Hit { get; private set; }
        public Task HitAsync(string name, CancellationToken ct = default)
        {
            if (name != targetName) return Task.CompletedTask;
            Hit = true;
            throw new InvalidOperationException($"Simulated crash at failpoint '{name}'.");
        }
    }

    private WebApplicationFactory<Program> MakeFactory(string hwmPath, string sinkDir, IFailpoints failpoints) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing"); // required for a non-no-op IFailpoints (spec MUST-NOT #33)
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev");
            builder.UseSetting("Coordinator:StateFilePath", hwmPath);
            builder.UseSetting("Allocator:Sink:LocalFileSystem:Directory", sinkDir);
            builder.ConfigureServices(services => services.AddSingleton(failpoints));
        });

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task Crash_after_hwm_fsync_before_commit_self_heals_on_restart()
    {
        await CrashAndRecoverAsync("coordinator.after-hwm-fsync");
    }

    [Fact]
    public async Task Crash_before_commit_self_heals_on_restart()
    {
        await CrashAndRecoverAsync("coordinator.before-commit");
    }

    [Fact]
    public async Task Crash_after_commit_before_response_does_not_lose_the_lease_on_restart()
    {
        await CrashAndRecoverAsync("coordinator.after-commit-before-response");
    }

    /// <summary>
    /// Proves R1/R8 at one exact named point: crash there, restart against the same HWM file, sink and
    /// database, and confirm leasing recovers to "ok" (the startup check's row-3 self-heal, already proven
    /// in isolation by Row3_database_behind_hwm_catches_up_and_stays_ok above) and a subsequent lease
    /// succeeds cleanly - no stuck "blocked" state, no exception on the next attempt.
    /// </summary>
    private async Task CrashAndRecoverAsync(string failpointName)
    {
        var fingerprint = "test-fingerprint";
        var hwmPath = Path.Combine(_tempDir, $"{failpointName.Replace('.', '-')}-hwm.json");
        var sinkDir = Path.Combine(_tempDir, $"{failpointName.Replace('.', '-')}-sink");
        var throwing = new ThrowAtFailpoint(failpointName);

        await using (var factory1 = MakeFactory(hwmPath, sinkDir, throwing))
        {
            var client = factory1.CreateClient();
            var initResponse = await client.PostAsJsonAsync("/v1/admin/init", new { Operator = "test", KeyFingerprint = fingerprint });
            Assert.True(initResponse.IsSuccessStatusCode, await initResponse.Content.ReadAsStringAsync());

            // TestServer rethrows an unhandled request-pipeline exception to the caller rather than
            // turning it into a 500 (there's no UseExceptionHandler in this coordinator) - the thrown
            // exception itself IS the "failed request" here, same as a real crashed process never
            // responding.
            await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsJsonAsync("/v1/leases",
                new { RequestId = Guid.NewGuid(), NodeId = "n1", PoolId = "default", KeyFingerprint = fingerprint }));
            Assert.True(throwing.Hit);
        }

        // "Restart": a fresh host (fresh StartupCheck.EvaluateAsync run, per Program.cs's own startup
        // line), same HWM file/sink/database, no-op failpoints from here on.
        await using var factory2 = MakeFactory(hwmPath, sinkDir, new NoOpFailpoints());
        var client2 = factory2.CreateClient();

        var status = await ReadJsonAsync(await client2.GetAsync("/v1/status"));
        Assert.Equal("ok", status.GetProperty("leasing").GetString());

        var secondLease = await client2.PostAsJsonAsync("/v1/leases",
            new { RequestId = Guid.NewGuid(), NodeId = "n1", PoolId = "default", KeyFingerprint = fingerprint });
        Assert.True(secondLease.IsSuccessStatusCode, await secondLease.Content.ReadAsStringAsync());
    }
}
