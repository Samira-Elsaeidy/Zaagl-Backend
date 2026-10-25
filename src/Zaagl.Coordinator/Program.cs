using System.Text.Json;
using Npgsql;
using Zaagl.SharedKernel.ShortCodes;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var stateFilePath = builder.Configuration["Coordinator:StateFilePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "coordinator-state.json");

await using var dataSource = NpgsqlDataSource.Create(connectionString);

// ---- Bootstrap: schema, the one pool-state row, and each length's seeded range -----------------
// Seeded once; after that, ranges change only through an admin action (out of scope for this build -
// spec §6.1's PUT /v1/admin/pools/.../ranges/{length} is not implemented here).
await using (var conn = await dataSource.OpenConnectionAsync())
{
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS coordinator;
            CREATE TABLE IF NOT EXISTS coordinator.pool_state (
                singleton boolean PRIMARY KEY DEFAULT true,
                current_length smallint NOT NULL,
                next_offset bigint NOT NULL,
                CONSTRAINT pool_state_singleton CHECK (singleton)
            );
            CREATE TABLE IF NOT EXISTS coordinator.length_ranges (
                code_length smallint PRIMARY KEY,
                range_end bigint NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            INSERT INTO coordinator.pool_state (singleton, current_length, next_offset)
            VALUES (true, $1, 0)
            ON CONFLICT (singleton) DO NOTHING
            """;
        cmd.Parameters.AddWithValue((short)CodeLengths.MinLength);
        await cmd.ExecuteNonQueryAsync();
    }

    for (var length = CodeLengths.MinLength; length <= CodeLengths.MaxLength; length++)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO coordinator.length_ranges (code_length, range_end)
            VALUES ($1, $2)
            ON CONFLICT (code_length) DO NOTHING
            """;
        cmd.Parameters.AddWithValue((short)length);
        cmd.Parameters.AddWithValue(CodeLengths.RangeEndOnePercent(length));
        await cmd.ExecuteNonQueryAsync();
    }
}

// ---- Restore safety: fail closed if the database looks behind what we last recorded on our own
// disk. A database restore can roll (length, offset) backward; our own high-water-mark file cannot be
// rolled back by restoring Postgres. "Behind" compares (length, offset) lexicographically: a smaller
// length is behind regardless of its offset; the same length with a smaller offset is behind.
CoordinatorState dbState;
await using (var conn = await dataSource.OpenConnectionAsync())
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT current_length, next_offset FROM coordinator.pool_state";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    dbState = new CoordinatorState(reader.GetInt16(0), reader.GetInt64(1));
}

var stateDir = Path.GetDirectoryName(stateFilePath);
if (!string.IsNullOrEmpty(stateDir)) Directory.CreateDirectory(stateDir);

if (File.Exists(stateFilePath))
{
    var recorded = JsonSerializer.Deserialize<CoordinatorState>(await File.ReadAllTextAsync(stateFilePath))
        ?? throw new InvalidOperationException($"{stateFilePath} exists but could not be parsed.");

    if (IsBehind(dbState, recorded))
    {
        Console.Error.WriteLine(
            $"FATAL: the database's position (length {dbState.Length}, offset {dbState.Offset}) is behind " +
            $"this coordinator's own recorded high-water mark (length {recorded.Length}, offset {recorded.Offset}). " +
            "This looks like a database restore to a point before numbers already leased from here. Refusing " +
            "to start: leasing now would risk handing out numbers a second time. Resolve by hand before " +
            "restarting this coordinator.");
        Environment.Exit(1);
        return;
    }
}
else
{
    await WriteStateAsync(stateFilePath, dbState);
}

var app = builder.Build();

var gate = new SemaphoreSlim(1, 1);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/lease", async () =>
{
    await gate.WaitAsync();
    try
    {
        CoordinatorState afterLease;
        long start;
        int length;
        int size;

        await using (var conn = await dataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            int currentLength;
            long nextOffset;
            await using (var cmd = new NpgsqlCommand(
                "SELECT current_length, next_offset FROM coordinator.pool_state FOR UPDATE", conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                currentLength = reader.GetInt16(0);
                nextOffset = reader.GetInt64(1);
            }

            var rangeEnd = await RangeEndAsync(conn, tx, currentLength);
            if (nextOffset >= rangeEnd)
            {
                // This length is exhausted: advance to the next one (spec §6.2 step 6, simplified -
                // no off-host length-marker write; that durability mechanism is out of scope here).
                var nextLength = currentLength + 1;
                if (nextLength > CodeLengths.MaxLength)
                {
                    await tx.RollbackAsync();
                    return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                }
                currentLength = nextLength;
                nextOffset = 0;
                rangeEnd = await RangeEndAsync(conn, tx, currentLength);
            }

            length = currentLength;
            size = CodeLengths.BlockSize(length);
            start = nextOffset;
            var end = Math.Min(start + size, rangeEnd);
            size = (int)(end - start);

            await using (var cmd = new NpgsqlCommand(
                "UPDATE coordinator.pool_state SET current_length = $1, next_offset = $2", conn, tx))
            {
                cmd.Parameters.AddWithValue((short)length);
                cmd.Parameters.AddWithValue(end);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            afterLease = new CoordinatorState(length, end);
        }

        // Write-ahead: the high-water mark lands on our own disk before this lease is handed to
        // anyone, so a crash right after this point still leaves an accurate record of what was
        // promised.
        await WriteStateAsync(stateFilePath, afterLease);

        return Results.Ok(new { length, start, size });
    }
    finally
    {
        gate.Release();
    }
});

app.Run();

static async Task<long> RangeEndAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int length)
{
    await using var cmd = new NpgsqlCommand(
        "SELECT range_end FROM coordinator.length_ranges WHERE code_length = $1", conn, tx);
    cmd.Parameters.AddWithValue((short)length);
    var result = await cmd.ExecuteScalarAsync()
        ?? throw new InvalidOperationException($"No seeded range for code length {length}.");
    return (long)result;
}

static bool IsBehind(CoordinatorState db, CoordinatorState recorded) =>
    db.Length < recorded.Length || (db.Length == recorded.Length && db.Offset < recorded.Offset);

static async Task WriteStateAsync(string path, CoordinatorState state)
{
    var json = JsonSerializer.Serialize(state);
    var tmp = path + ".tmp";
    await File.WriteAllTextAsync(tmp, json);
    File.Move(tmp, path, overwrite: true);
}

/// <summary>The coordinator's one piece of durable position: how far it has promised, as (length, offset within that length).</summary>
internal sealed record CoordinatorState(int Length, long Offset);
