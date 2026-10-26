using Npgsql;
using Zaagl.Coordinator;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

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
}
