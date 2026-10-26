using Npgsql;
using Zaagl.SharedKernel.Coordination;

namespace Zaagl.Coordinator;

public enum LeasingState { NotInitialised, Blocked, Ok }

public sealed record StartupResult(LeasingState State, string? Reason, string? InstallationId, string? KeyFingerprint, DateTimeOffset? RestoreDetectedAt, string Row);

/// <summary>
/// The startup decision table of spec §6.4, evaluated for pool "default" (the only pool this build
/// supports, per spec: "'default' in year 1"). Pure decision logic plus the one self-healing action the
/// spec allows automatically (row 2: finishing an interrupted init, and row 3: catching the database up
/// to the HWM after a normal restart) - every other row only blocks and waits for an operator-driven
/// recovery call (spec MUST-NOT #2: "initialise or recover automatically except the interrupted-init case").
/// </summary>
public static class StartupCheck
{
    public const string PoolId = "default";

    public static async Task<StartupResult> EvaluateAsync(
        NpgsqlDataSource dataSource, HwmStore hwmStore, IAllocatorSink sink, CancellationToken ct)
    {
        HwmFile? hwm;
        try { hwm = await hwmStore.ReadAsync(ct); }
        catch (Exception)
        {
            // HWM unreadable or bad checksum -> row 7.
            return new StartupResult(LeasingState.Blocked, "hwm-corrupt", null, null, null, "7");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (installationId, keyFingerprint) = await ReadInstallationAsync(conn, ct);
        var hasLeases = await HasAnyLeasesAsync(conn, ct);
        var (dbLength, dbOffset) = await ReadPoolPositionAsync(conn, PoolId, ct);

        bool sinkEmpty;
        InstallationManifest? manifest;
        try
        {
            sinkEmpty = await sink.IsEmptyAsync(ct);
            manifest = sinkEmpty ? null : await sink.ReadManifestAsync(ct);
        }
        catch (Exception)
        {
            // Row 1b: sink unreachable while no installation row exists.
            if (installationId is null)
                return new StartupResult(LeasingState.Blocked, "sink-unreachable", null, null, null, "1b");
            throw;
        }

        // Row 1 / 1a: nothing local or in the database yet.
        if (hwm is null && installationId is null && !hasLeases)
        {
            if (sinkEmpty)
                return new StartupResult(LeasingState.NotInitialised, null, null, null, null, "1");

            // Row 1a: a database from before initialisation was restored after codes were already
            // issued elsewhere - the sink remembers an installation this database has never heard of.
            return new StartupResult(LeasingState.Blocked, "history-found", manifest?.InstallationId, manifest?.KeyFingerprint, null, "1a");
        }

        // Row 2: interrupted init. Finish it automatically (the one case the spec allows).
        if (hwm is { State: "initializing" } && !hasLeases
            && (installationId is null || (installationId == hwm.InstallationId && keyFingerprint == hwm.KeyFingerprint)))
        {
            await FinishInterruptedInitAsync(conn, hwmStore, sink, hwm, ct);
            return new StartupResult(LeasingState.Ok, null, hwm.InstallationId, hwm.KeyFingerprint, null, "2");
        }

        // Row 6: HWM says active, but the installation row is gone (restored to before init, or replaced).
        if (hwm is { State: "active" } && installationId is null)
            return new StartupResult(LeasingState.Blocked, "installation-missing", hwm.InstallationId, hwm.KeyFingerprint, null, "6");

        // Row 7: HWM missing while the database is already initialised, or HWM/installation disagree.
        if (hwm is null && installationId is not null)
            return new StartupResult(LeasingState.Blocked, "hwm-missing", installationId, keyFingerprint, null, "7");

        if (hwm is { State: "active" } && installationId is not null
            && (hwm.InstallationId != installationId || hwm.KeyFingerprint != keyFingerprint))
            return new StartupResult(LeasingState.Blocked, "identity-mismatch", installationId, keyFingerprint, null, "7");

        // Rows 3/4/5: HWM and installation agree - compare the database position against the HWM.
        if (hwm is { State: "active" } && installationId is not null && hwm.Pools.TryGetValue(PoolId, out var poolHwm))
        {
            var cmp = CompareLengthOffset(dbLength, dbOffset, poolHwm.Length, poolHwm.ReservedOffset);
            if (cmp < 0)
            {
                // Row 3: database behind HWM - normal after every restart. Catch it up.
                await SetPoolPositionAsync(conn, PoolId, poolHwm.Length, poolHwm.ReservedOffset, ct);
                var restoreDetected = IsBeyondOneReservation(dbLength, dbOffset, poolHwm.Length, poolHwm.ReservedOffset);
                DateTimeOffset? detectedAt = null;
                if (restoreDetected)
                {
                    detectedAt = DateTimeOffset.UtcNow;
                    await SetRestoreDetectedAsync(conn, PoolId, detectedAt.Value, ct);
                }
                return new StartupResult(LeasingState.Ok, null, installationId, keyFingerprint, detectedAt, "3");
            }
            if (cmp == 0)
                return new StartupResult(LeasingState.Ok, null, installationId, keyFingerprint, null, "4");

            // Row 5: database ahead of HWM - the HWM-before-commit invariant was violated.
            return new StartupResult(LeasingState.Blocked, "hwm-behind-database", installationId, keyFingerprint, null, "5");
        }

        // Shouldn't normally be reached; treat as row 7 (disagreement / corrupt state).
        return new StartupResult(LeasingState.Blocked, "unrecognised-state", installationId, keyFingerprint, null, "7");
    }

    private static int CompareLengthOffset(int l1, long o1, int l2, long o2) =>
        l1 != l2 ? l1.CompareTo(l2) : o1.CompareTo(o2);

    private static bool IsBeyondOneReservation(int dbLength, long dbOffset, int hwmLength, long hwmOffset) =>
        dbLength != hwmLength || hwmOffset - dbOffset > 0; // conservative: any gap at a different length, or any offset gap, is flagged for visibility

    private static async Task FinishInterruptedInitAsync(NpgsqlConnection conn, HwmStore hwmStore, IAllocatorSink sink, HwmFile hwm, CancellationToken ct)
    {
        var installationId = hwm.InstallationId ?? throw new InvalidOperationException("Interrupted init HWM has no installationId.");
        var keyFingerprint = hwm.KeyFingerprint ?? throw new InvalidOperationException("Interrupted init HWM has no keyFingerprint.");

        await sink.TryWriteManifestAsync(new InstallationManifest(installationId, keyFingerprint, PoolId, CodeLengthsStart, DateTimeOffset.UtcNow), ct);
        await sink.TryWriteLengthMarkerAsync(new LengthMarker(installationId, keyFingerprint, PoolId, CodeLengthsStart, DateTimeOffset.UtcNow), ct);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO alloc.installation (installation_id, key_fingerprint, initialized_at) VALUES ($1, $2, now()) ON CONFLICT DO NOTHING",
            conn))
        {
            cmd.Parameters.AddWithValue(Guid.Parse(installationId));
            cmd.Parameters.AddWithValue(keyFingerprint);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var active = hwm with { State = "active" };
        await hwmStore.WriteAsync(active, ct);
    }

    private const int CodeLengthsStart = Zaagl.SharedKernel.ShortCodes.CodeLengths.MinLength;

    private static async Task<(string? InstallationId, string? KeyFingerprint)> ReadInstallationAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT installation_id, key_fingerprint FROM alloc.installation LIMIT 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (null, null);
        return (reader.GetGuid(0).ToString(), reader.GetString(1));
    }

    private static async Task<bool> HasAnyLeasesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM alloc.leases LIMIT 1", conn);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public static async Task<(int Length, long Offset)> ReadPoolPositionAsync(NpgsqlConnection conn, string poolId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_length, next_offset FROM alloc.pools WHERE pool_id = $1", conn);
        cmd.Parameters.AddWithValue(poolId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt16(0), reader.GetInt64(1));
    }

    private static async Task SetPoolPositionAsync(NpgsqlConnection conn, string poolId, int length, long offset, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE alloc.pools SET current_length = $1, next_offset = $2, updated_at = now() WHERE pool_id = $3", conn);
        cmd.Parameters.AddWithValue((short)length);
        cmd.Parameters.AddWithValue(offset);
        cmd.Parameters.AddWithValue(poolId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetRestoreDetectedAsync(NpgsqlConnection conn, string poolId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE alloc.pools SET restore_detected_at = $1 WHERE pool_id = $2", conn);
        cmd.Parameters.AddWithValue(at);
        cmd.Parameters.AddWithValue(poolId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
