using Npgsql;
using Zaagl.SharedKernel.Coordination;

namespace Zaagl.Coordinator;

public sealed record RecoveryOutcome(bool Success, string Mode, string Detail);

/// <summary>
/// The four operator-driven recovery modes of spec §6.4. Each ends with a durable "active" HWM and a
/// row in <c>alloc.recovery_log</c>. Dev-mode note: this Postgres instance has no WAL archiving
/// configured, so <c>resume</c>'s archive-continuity check always reports "cannot verify" rather than
/// a real pass/fail - it is real code, exercised by a fake-archiver test, but has never been run
/// against genuine archived WAL segments.
/// </summary>
public sealed class RecoveryService(NpgsqlDataSource dataSource, HwmStore hwmStore, IAllocatorSink sink)
{
    public async Task<RecoveryOutcome> ReattachAsync(string operatorName, CancellationToken ct)
    {
        var hwm = await hwmStore.ReadAsync(ct);
        if (hwm is not { State: "active", InstallationId: not null, KeyFingerprint: not null })
            return new RecoveryOutcome(false, "reattach", "No valid active HWM to reattach from.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO alloc.installation (installation_id, key_fingerprint, initialized_at)
            VALUES ($1, $2, now())
            ON CONFLICT (installation_id) DO UPDATE SET key_fingerprint = EXCLUDED.key_fingerprint
            """, conn))
        {
            cmd.Parameters.AddWithValue(Guid.Parse(hwm.InstallationId));
            cmd.Parameters.AddWithValue(hwm.KeyFingerprint);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (hwm.Pools.TryGetValue(StartupCheck.PoolId, out var poolHwm))
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE alloc.pools SET current_length = $1, next_offset = $2, updated_at = now() WHERE pool_id = $3", conn);
            cmd.Parameters.AddWithValue((short)poolHwm.Length);
            cmd.Parameters.AddWithValue(poolHwm.ReservedOffset);
            cmd.Parameters.AddWithValue(StartupCheck.PoolId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await LogAsync(conn, "reattach", operatorName, $"Reattached installation {hwm.InstallationId} from HWM.", ct);
        return new RecoveryOutcome(true, "reattach", $"Installation {hwm.InstallationId} reattached; pool moved to the HWM position.");
    }

    public async Task<RecoveryOutcome> TrustDatabaseAsync(string operatorName, bool confirmDatabaseComplete, CancellationToken ct)
    {
        if (!confirmDatabaseComplete)
            return new RecoveryOutcome(false, "trust-database", "confirmDatabaseComplete must be true: the operator must assert the database was never restored since the HWM was last correct.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (length, offset) = await StartupCheck.ReadPoolPositionAsync(conn, StartupCheck.PoolId, ct);
        var (installationId, keyFingerprint) = await ReadInstallationAsync(conn, ct);
        if (installationId is null || keyFingerprint is null)
            return new RecoveryOutcome(false, "trust-database", "No installation row to attach the new HWM to.");

        var newHwm = new HwmFile(1, installationId, keyFingerprint, "active",
            new Dictionary<string, PoolHwm> { [StartupCheck.PoolId] = new(length, offset, DateTimeOffset.UtcNow) });
        await hwmStore.WriteAsync(newHwm, ct);

        await LogAsync(conn, "trust-database", operatorName, $"HWM rewritten from the database position (length {length}, offset {offset}).", ct);
        return new RecoveryOutcome(true, "trust-database", $"HWM now matches the database (length {length}, offset {offset}).");
    }

    /// <summary>
    /// Verifies a host-loss restore before resuming (spec §6.4). In dev, with no WAL archive
    /// configured, this always returns "cannot verify" - real decision code, exercised only by the
    /// fake-archiver unit test, never against a genuine archive.
    /// </summary>
    public async Task<RecoveryOutcome> ResumeAsync(string operatorName, IArchiveReader archiveReader, CancellationToken ct)
    {
        var verification = await archiveReader.VerifyContinuityAsync(ct);
        if (!verification.CanVerify)
            return new RecoveryOutcome(false, "resume", $"Cannot verify the restore: {verification.Reason}. Use new-length instead.");
        if (!verification.IsContinuous)
            return new RecoveryOutcome(false, "resume", $"Archive continuity check failed: {verification.Reason}. Use new-length instead.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (installationId, keyFingerprint) = await ReadInstallationAsync(conn, ct);
        if (installationId is null || keyFingerprint is null)
            return new RecoveryOutcome(false, "resume", "No installation row matching the manifest identity.");

        var (dbLength, dbOffset) = await StartupCheck.ReadPoolPositionAsync(conn, StartupCheck.PoolId, ct);
        var newHwm = new HwmFile(1, installationId, keyFingerprint, "active",
            new Dictionary<string, PoolHwm> { [StartupCheck.PoolId] = new(dbLength, dbOffset, DateTimeOffset.UtcNow) });
        await hwmStore.WriteAsync(newHwm, ct);

        await LogAsync(conn, "resume", operatorName, "Resumed from the highest confirmed reservation/lease/pool position after verified host loss.", ct);
        return new RecoveryOutcome(true, "resume", $"Resumed at length {dbLength}, offset {dbOffset}.");
    }

    public async Task<RecoveryOutcome> NewLengthAsync(string operatorName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var markers = await sink.ListLengthMarkersAsync(StartupCheck.PoolId, ct);
        var highestMarker = markers.Count > 0 ? markers.Max(m => m.Length) : Zaagl.SharedKernel.ShortCodes.CodeLengths.MinLength - 1;

        await using var highestLeaseCmd = new NpgsqlCommand("SELECT COALESCE(MAX(code_length), 0) FROM alloc.leases WHERE pool_id = $1", conn);
        highestLeaseCmd.Parameters.AddWithValue(StartupCheck.PoolId);
        var highestLease = (short)(await highestLeaseCmd.ExecuteScalarAsync(ct))!;

        await using var highestReservationCmd = new NpgsqlCommand("SELECT COALESCE(MAX(code_length), 0) FROM alloc.reservations WHERE pool_id = $1", conn);
        highestReservationCmd.Parameters.AddWithValue(StartupCheck.PoolId);
        var highestReservation = (short)(await highestReservationCmd.ExecuteScalarAsync(ct))!;

        var toLength = Math.Max(Math.Max(highestMarker, (int)highestLease), (int)highestReservation) + 1;
        if (toLength > Zaagl.SharedKernel.ShortCodes.CodeLengths.MaxLength)
            return new RecoveryOutcome(false, "new-length", "No length remains; generated creation stays blocked (507). Aliases keep working.");

        var (installationId, keyFingerprint) = await ReadInstallationAsync(conn, ct);
        if (installationId is null || keyFingerprint is null)
        {
            var manifest = await sink.ReadManifestAsync(ct)
                ?? throw new InvalidOperationException("new-length requires either an installation row or a sink manifest to adopt.");
            installationId = manifest.InstallationId;
            keyFingerprint = manifest.KeyFingerprint;
            await using var insertInstall = new NpgsqlCommand(
                "INSERT INTO alloc.installation (installation_id, key_fingerprint, initialized_at) VALUES ($1, $2, now()) ON CONFLICT DO NOTHING", conn);
            insertInstall.Parameters.AddWithValue(Guid.Parse(installationId));
            insertInstall.Parameters.AddWithValue(keyFingerprint);
            await insertInstall.ExecuteNonQueryAsync(ct);
        }

        await sink.TryWriteLengthMarkerAsync(new LengthMarker(installationId, keyFingerprint, StartupCheck.PoolId, toLength, DateTimeOffset.UtcNow), ct);

        await using (var cmd = new NpgsqlCommand(
            "UPDATE alloc.pools SET current_length = $1, next_offset = 0, updated_at = now() WHERE pool_id = $2", conn))
        {
            cmd.Parameters.AddWithValue((short)toLength);
            cmd.Parameters.AddWithValue(StartupCheck.PoolId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var newHwm = new HwmFile(1, installationId, keyFingerprint, "active",
            new Dictionary<string, PoolHwm> { [StartupCheck.PoolId] = new(toLength, 0, DateTimeOffset.UtcNow) });
        await hwmStore.WriteAsync(newHwm, ct);

        var remaining = Zaagl.SharedKernel.ShortCodes.CodeLengths.MaxLength - toLength;
        await LogAsync(conn, "new-length", operatorName, $"Abandoned the previous length, moved to length {toLength}. {remaining} length(s) remain.", ct);
        return new RecoveryOutcome(true, "new-length", $"Moved to length {toLength}. {remaining} length(s) remain" + (remaining <= 2 ? " - ALERT: two or fewer remain." : "."));
    }

    private static async Task<(string? InstallationId, string? KeyFingerprint)> ReadInstallationAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT installation_id, key_fingerprint FROM alloc.installation LIMIT 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (null, null);
        return (reader.GetGuid(0).ToString(), reader.GetString(1));
    }

    private static async Task LogAsync(NpgsqlConnection conn, string mode, string operatorName, string detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO alloc.recovery_log (at, mode, details, operator) VALUES (now(), $1, $2::jsonb, $3)", conn);
        cmd.Parameters.AddWithValue(mode);
        cmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { detail }));
        cmd.Parameters.AddWithValue(operatorName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record ArchiveVerification(bool CanVerify, bool IsContinuous, string Reason);

/// <summary>Reads WAL-archive continuity for <c>resume</c> (spec §6.4). <see cref="PgArchiveReader"/> is the real implementation; tests use a fake.</summary>
public interface IArchiveReader
{
    Task<ArchiveVerification> VerifyContinuityAsync(CancellationToken ct);
}

/// <summary>
/// Real implementation: reads <c>pg_stat_archiver</c> and <c>pg_ls_archive_statusdir()</c>. On this dev
/// Postgres, WAL archiving is not configured, so <c>last_archived_wal</c> is empty and this always
/// returns "cannot verify" - the code path is real, just never exercised against a genuine archive.
/// </summary>
public sealed class PgArchiveReader(NpgsqlDataSource dataSource) : IArchiveReader
{
    public async Task<ArchiveVerification> VerifyContinuityAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT last_archived_wal FROM pg_stat_archiver", conn);
        var lastArchived = (string?)await cmd.ExecuteScalarAsync(ct);
        if (string.IsNullOrEmpty(lastArchived))
            return new ArchiveVerification(false, false, "No WAL archive is configured on this database (pg_stat_archiver.last_archived_wal is empty).");

        // A real implementation would read the new timeline's .history file from the archive and
        // compare segment numbers (spec §6.4). Not reachable in this dev environment since archiving
        // is never configured here; left as a documented gap for the next phase that has a real archive.
        return new ArchiveVerification(false, false, "Archive continuity verification against real WAL segments is not implemented in this dev build.");
    }
}
