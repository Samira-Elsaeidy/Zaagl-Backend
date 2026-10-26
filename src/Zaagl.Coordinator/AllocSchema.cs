using Npgsql;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Coordinator;

/// <summary>
/// Bootstraps the <c>alloc</c> schema (spec §4.2) with raw SQL rather than EF Core migrations - a
/// deliberate scoping decision for this phase: the coordinator has no EF Core dependency today, and
/// introducing a full DbContext/migration-history/design-time-factory surface for one schema was
/// judged not worth the added weight versus idempotent, re-runnable DDL. A future phase can migrate
/// this to EF Core migrations without changing the schema itself.
/// </summary>
internal static class AllocSchema
{
    public static async Task EnsureCreatedAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE EXTENSION IF NOT EXISTS btree_gist;
                CREATE SCHEMA IF NOT EXISTS alloc;

                CREATE TABLE IF NOT EXISTS alloc.pools (
                    pool_id text PRIMARY KEY,
                    current_length smallint NOT NULL,
                    next_offset bigint NOT NULL,
                    restore_detected_at timestamptz NULL,
                    updated_at timestamptz NOT NULL
                );

                CREATE TABLE IF NOT EXISTS alloc.pool_ranges (
                    pool_id text NOT NULL REFERENCES alloc.pools,
                    code_length smallint NOT NULL CHECK (code_length BETWEEN 5 AND 10),
                    range_start bigint NOT NULL,
                    range_end bigint NOT NULL,
                    key_version smallint NOT NULL DEFAULT 1 CHECK (key_version = 1),
                    block_size int NOT NULL CHECK (block_size BETWEEN 100 AND 1000000),
                    reserve_ahead_blocks int NOT NULL DEFAULT 10 CHECK (reserve_ahead_blocks BETWEEN 1 AND 100),
                    PRIMARY KEY (pool_id, code_length),
                    CHECK (range_start >= 0 AND range_end > range_start)
                );

                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'pool_ranges_no_overlap') THEN
                        ALTER TABLE alloc.pool_ranges ADD CONSTRAINT pool_ranges_no_overlap
                            EXCLUDE USING gist (code_length WITH =, int8range(range_start, range_end) WITH &&);
                    END IF;
                END $$;

                CREATE TABLE IF NOT EXISTS alloc.leases (
                    lease_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    request_id uuid NOT NULL UNIQUE,
                    pool_id text NOT NULL,
                    node_id text NOT NULL,
                    code_length smallint NOT NULL,
                    key_version smallint NOT NULL,
                    start_number bigint NOT NULL,
                    end_number bigint NOT NULL,
                    issued_at timestamptz NOT NULL,
                    CHECK (end_number > start_number)
                );

                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'leases_no_overlap') THEN
                        ALTER TABLE alloc.leases ADD CONSTRAINT leases_no_overlap
                            EXCLUDE USING gist (code_length WITH =, int8range(start_number, end_number) WITH &&);
                    END IF;
                END $$;

                CREATE TABLE IF NOT EXISTS alloc.reservations (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    pool_id text NOT NULL,
                    code_length smallint NOT NULL,
                    reserved_offset bigint NOT NULL,
                    created_at timestamptz NOT NULL,
                    commit_lsn pg_lsn NULL,
                    confirmed_at timestamptz NULL
                );
                CREATE INDEX IF NOT EXISTS ix_reservations_pool_length_offset
                    ON alloc.reservations (pool_id, code_length, reserved_offset);

                CREATE TABLE IF NOT EXISTS alloc.installation (
                    installation_id uuid PRIMARY KEY,
                    key_fingerprint text NOT NULL,
                    initialized_at timestamptz NOT NULL
                );

                CREATE TABLE IF NOT EXISTS alloc.recovery_log (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    at timestamptz NOT NULL,
                    mode text NOT NULL,
                    details jsonb NOT NULL,
                    operator text NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Seed pool_ranges for pool 'default', all six lengths, per the spec §4.2 table. Idempotent:
        // only inserted if the pool row doesn't exist yet (ranges change only through the admin
        // endpoint after that, per spec: "The database is authoritative for ranges").
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM alloc.pools WHERE pool_id = 'default'";
            var exists = await cmd.ExecuteScalarAsync(ct) is not null;
            if (!exists)
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var insertPool = new NpgsqlCommand(
                    "INSERT INTO alloc.pools (pool_id, current_length, next_offset, updated_at) VALUES ('default', $1, 0, now())",
                    conn, tx))
                {
                    insertPool.Parameters.AddWithValue((short)CodeLengths.MinLength);
                    await insertPool.ExecuteNonQueryAsync(ct);
                }

                for (var length = CodeLengths.MinLength; length <= CodeLengths.MaxLength; length++)
                {
                    await using var insertRange = new NpgsqlCommand(
                        """
                        INSERT INTO alloc.pool_ranges (pool_id, code_length, range_start, range_end, key_version, block_size, reserve_ahead_blocks)
                        VALUES ('default', $1, 0, $2, 1, $3, 10)
                        """, conn, tx);
                    insertRange.Parameters.AddWithValue((short)length);
                    insertRange.Parameters.AddWithValue(CodeLengths.RangeEndOnePercent(length));
                    insertRange.Parameters.AddWithValue(CodeLengths.BlockSize(length));
                    await insertRange.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
        }
    }
}
