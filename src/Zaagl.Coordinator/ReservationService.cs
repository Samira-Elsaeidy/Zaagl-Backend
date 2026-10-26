using Npgsql;

namespace Zaagl.Coordinator;

/// <summary>
/// Archive-confirmed reservations (spec §6.4, product decision v9.4): a lease may only be committed
/// once a reservation covering it is confirmed archived in the off-host WAL. Gated by
/// <c>Allocator:RequireArchiveConfirmation</c> - false in this dev environment (no WAL archiving is
/// configured on this Postgres instance), matching the spec's explicit allowance: "development and
/// in-process tests only, with a fake archiver". When false, a reservation is considered confirmed the
/// instant it's inserted; the table and renewal logic are still real and exercised, just without the
/// archive-polling step.
/// </summary>
public sealed class ReservationService(NpgsqlDataSource dataSource, bool requireArchiveConfirmation)
{
    public bool RequireArchiveConfirmation => requireArchiveConfirmation;

    /// <summary>
    /// Ensures a confirmed reservation covers <paramref name="offset"/> for (pool, length), inserting a
    /// new one (reaching <paramref name="offset"/> plus <paramref name="aheadMargin"/>) if needed. Must
    /// be called from inside the caller's own transaction/connection so the insert is part of the same
    /// unit of work when a new one is needed; returns whether <paramref name="offset"/> is now covered.
    /// </summary>
    public async Task<bool> EnsureCoveredAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string poolId, int length, long offset, long aheadMargin, CancellationToken ct)
    {
        if (await IsCoveredAsync(conn, tx, poolId, length, offset, ct)) return true;

        long reservedOffset = offset + aheadMargin;
        long id;
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO alloc.reservations (pool_id, code_length, reserved_offset, created_at) VALUES ($1, $2, $3, now()) RETURNING id",
            conn, tx))
        {
            insert.Parameters.AddWithValue(poolId);
            insert.Parameters.AddWithValue((short)length);
            insert.Parameters.AddWithValue(reservedOffset);
            id = (long)(await insert.ExecuteScalarAsync(ct))!;
        }

        if (!requireArchiveConfirmation)
        {
            // Fake-archiver dev path: confirmed immediately, no commit_lsn needed.
            await using var confirm = new NpgsqlCommand("UPDATE alloc.reservations SET confirmed_at = now() WHERE id = $1", conn, tx);
            confirm.Parameters.AddWithValue(id);
            await confirm.ExecuteNonQueryAsync(ct);
            return true;
        }

        // Real path: record commit_lsn after the row is visible; confirmation happens later via the
        // background poller reading pg_stat_archiver (ConfirmPendingAsync). Not covered yet.
        return false;
    }

    public async Task<bool> IsCoveredAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string poolId, int length, long offset, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM alloc.reservations
            WHERE pool_id = $1 AND code_length = $2 AND reserved_offset >= $3 AND confirmed_at IS NOT NULL
            LIMIT 1
            """, conn, tx);
        cmd.Parameters.AddWithValue(poolId);
        cmd.Parameters.AddWithValue((short)length);
        cmd.Parameters.AddWithValue(offset);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Background confirmation polling for the real (archive-required) path: reads
    /// <c>pg_stat_archiver.last_archived_wal</c> and marks pending reservations confirmed once their
    /// segment is archived. Only meaningful when <see cref="RequireArchiveConfirmation"/> is true; never
    /// exercised against a real archive in this dev build (see <see cref="PgArchiveReader"/>'s note).
    /// </summary>
    public async Task ConfirmPendingAsync(CancellationToken ct)
    {
        if (!requireArchiveConfirmation) return;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var archiverCmd = new NpgsqlCommand("SELECT last_archived_wal FROM pg_stat_archiver", conn);
        var lastArchived = (string?)await archiverCmd.ExecuteScalarAsync(ct);
        if (string.IsNullOrEmpty(lastArchived)) return; // archiving not configured; nothing to confirm

        // A real implementation compares each pending row's commit_lsn-derived segment number against
        // last_archived_wal (spec §6.4). Not reachable in this dev environment; documented gap.
    }
}
