using Npgsql;
using Zaagl.Coordinator;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var stateFilePath = builder.Configuration["Coordinator:StateFilePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "coordinator-state.json");
var runtimeToken = builder.Configuration["Coordinator:Token"] ?? "";
var adminToken = builder.Configuration["Coordinator:AdminToken"] ?? "";
var requireArchiveConfirmation = builder.Configuration.GetValue<bool?>("Allocator:RequireArchiveConfirmation") ?? false;
var sinkType = builder.Configuration["Allocator:Sink:Type"] ?? "LocalFileSystem";
var sinkDirectory = builder.Configuration["Allocator:Sink:LocalFileSystem:Directory"]
    ?? Path.Combine(AppContext.BaseDirectory, "allocator-sink");

builder.Services.AddSingleton<Zaagl.SharedKernel.Testing.IFailpoints, Zaagl.SharedKernel.Testing.NoOpFailpoints>();

var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(new HwmStore(stateFilePath));
builder.Services.AddSingleton<IAllocatorSink>(_ =>
{
    if (sinkType == "LocalFileSystem") return new LocalFileSystemAllocatorSink(sinkDirectory);
    throw new NotSupportedException(
        $"Allocator sink type '{sinkType}' is not built in this phase. Only LocalFileSystem exists; " +
        "production needs a real S3-compatible EU bucket implementation (spec §6.4), not yet written.");
});
builder.Services.AddSingleton(new ReservationService(dataSource, requireArchiveConfirmation));
builder.Services.AddSingleton<RecoveryService>();
builder.Services.AddSingleton<IArchiveReader, PgArchiveReader>();

await AllocSchema.EnsureCreatedAsync(dataSource, CancellationToken.None);

var app = builder.Build();

// Spec MUST-NOT #33.
if (app.Services.GetRequiredService<Zaagl.SharedKernel.Testing.IFailpoints>() is not Zaagl.SharedKernel.Testing.NoOpFailpoints
    && app.Environment.EnvironmentName != "Testing")
{
    throw new InvalidOperationException("A non-no-op IFailpoints implementation is registered outside the Testing environment.");
}

var hwmStore = app.Services.GetRequiredService<HwmStore>();
var sink = app.Services.GetRequiredService<IAllocatorSink>();
var reservations = app.Services.GetRequiredService<ReservationService>();
var recovery = app.Services.GetRequiredService<RecoveryService>();

var gate = new SemaphoreSlim(1, 1);

// ---- Startup check (spec §6.4 decision table) ----------------------------------------------------
var startup = await StartupCheck.EvaluateAsync(dataSource, hwmStore, sink, CancellationToken.None);
var leasingState = startup.State;
var leasingReason = startup.Reason;
Console.WriteLine($"Startup check: row {startup.Row} -> {leasingState}" + (leasingReason is not null ? $" ({leasingReason})" : ""));

// ---- Auth middleware: COORDINATOR_TOKEN for runtime routes, COORDINATOR_ADMIN_TOKEN for /v1/admin/* ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/health")) { await next(); return; }

    var required = path.StartsWith("/v1/admin/") ? adminToken : runtimeToken;
    if (!string.IsNullOrEmpty(required))
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header != $"Bearer {required}")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Unauthorized.");
            return;
        }
    }
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async () =>
{
    try { await using var conn = await dataSource.OpenConnectionAsync(); return Results.Ok(new { status = "ok" }); }
    catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" })); // legacy alias

app.MapGet("/v1/status", () => Results.Ok(new
{
    keyFingerprint = startup.KeyFingerprint,
    leasing = leasingState switch
    {
        LeasingState.NotInitialised => "not-initialised",
        LeasingState.Blocked => "blocked",
        _ => "ok",
    },
    leasingReason,
    restoreDetectedAt = startup.RestoreDetectedAt,
}));

app.MapGet("/v1/pools/{poolId}", async (string poolId) =>
{
    await using var conn = await dataSource.OpenConnectionAsync();
    var (length, offset) = await StartupCheck.ReadPoolPositionAsync(conn, poolId, CancellationToken.None);
    await using var rangeCmd = new NpgsqlCommand("SELECT range_end FROM alloc.pool_ranges WHERE pool_id = $1 AND code_length = $2", conn);
    rangeCmd.Parameters.AddWithValue(poolId);
    rangeCmd.Parameters.AddWithValue((short)length);
    var rangeEnd = (long)(await rangeCmd.ExecuteScalarAsync())!;

    await using var leaseCountCmd = new NpgsqlCommand(
        "SELECT COUNT(*) FROM alloc.leases WHERE pool_id = $1 AND issued_at > now() - interval '1 hour'", conn);
    leaseCountCmd.Parameters.AddWithValue(poolId);
    var leasesLastHour = (long)(await leaseCountCmd.ExecuteScalarAsync())!;

    return Results.Ok(new
    {
        poolId,
        currentLength = length,
        nextOffset = offset,
        rangeEnd,
        fillRatio = rangeEnd > 0 ? (double)offset / rangeEnd : 0,
        lengthsRemaining = CodeLengths.MaxLength - length,
        restoreDetectedAt = startup.RestoreDetectedAt,
        leasesLastHour,
    });
});

// ---- Runtime: lease (spec §6.2) --------------------------------------------------------------------
app.MapPost("/v1/leases", async (LeaseRequest body) =>
{
    if (leasingState == LeasingState.NotInitialised) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (leasingState == LeasingState.Blocked) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (startup.KeyFingerprint is not null && body.KeyFingerprint != startup.KeyFingerprint)
        return Results.Conflict(new { error = "key-mismatch" });

    await gate.WaitAsync();
    try
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Step 3: lock the pool row.
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM alloc.pools WHERE pool_id = $1 FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue(body.PoolId);
            if (await cmd.ExecuteScalarAsync() is null) { await tx.RollbackAsync(); return Results.NotFound(); }
        }

        // Step 4: idempotency on request_id.
        await using (var existingCmd = new NpgsqlCommand(
            "SELECT lease_id, pool_id, node_id, code_length, key_version, start_number, end_number FROM alloc.leases WHERE request_id = $1", conn, tx))
        {
            existingCmd.Parameters.AddWithValue(body.RequestId);
            await using var reader = await existingCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var existingPoolId = reader.GetString(1);
                var existingNodeId = reader.GetString(2);
                if (existingPoolId != body.PoolId || existingNodeId != body.NodeId)
                {
                    await tx.RollbackAsync();
                    return Results.Conflict(new { error = "request-id-reused-with-different-data" });
                }
                var result = new LeaseResponse(reader.GetInt64(0), existingPoolId, reader.GetInt16(3), reader.GetInt16(4), reader.GetInt64(5), reader.GetInt64(6));
                await tx.CommitAsync();
                return Results.Ok(result);
            }
        }

        var (currentLength, nextOffset) = await StartupCheck.ReadPoolPositionAsync(conn, body.PoolId, CancellationToken.None);
        var (rangeStart, rangeEnd, blockSize, reserveAheadBlocks) = await ReadRangeAsync(conn, tx, body.PoolId, currentLength);

        // Step 6: length rollover.
        var start = rangeStart + nextOffset;
        if (start >= rangeEnd)
        {
            var nextLength = currentLength + 1;
            if (nextLength > CodeLengths.MaxLength) { await tx.RollbackAsync(); return Results.StatusCode(StatusCodes.Status507InsufficientStorage); }

            var marker = new LengthMarker(startup.InstallationId ?? "", startup.KeyFingerprint ?? "", body.PoolId, nextLength, DateTimeOffset.UtcNow);
            if (!await sink.TryWriteLengthMarkerAsync(marker, CancellationToken.None))
            {
                await tx.RollbackAsync();
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            currentLength = nextLength;
            nextOffset = 0;
            (rangeStart, rangeEnd, blockSize, reserveAheadBlocks) = await ReadRangeAsync(conn, tx, body.PoolId, currentLength);
            start = rangeStart + nextOffset;
        }

        var end = Math.Min(start + blockSize, rangeEnd);

        // Step 7: HWM before commit - write the HWM reservation INSIDE this still-open transaction,
        // before it commits, so a crash between the write and the commit just rolls the DB back to a
        // position behind the HWM (row 3: normal, self-heals on next startup) rather than ever
        // committing a lease the HWM doesn't already cover.
        var aheadMargin = (long)blockSize * reserveAheadBlocks;
        var hwm = await hwmStore.ReadAsync(CancellationToken.None);
        var currentHwmOffset = hwm?.Pools.GetValueOrDefault(body.PoolId)?.ReservedOffset ?? 0;
        var currentHwmLength = hwm?.Pools.GetValueOrDefault(body.PoolId)?.Length ?? currentLength;
        var offsetWithinLength = end - rangeStart;
        if (currentHwmLength != currentLength || currentHwmOffset < offsetWithinLength)
        {
            var newReservedOffset = Math.Min(offsetWithinLength + aheadMargin, rangeEnd - rangeStart);
            var newHwm = new HwmFile(1, startup.InstallationId, startup.KeyFingerprint, "active",
                new Dictionary<string, PoolHwm>((hwm?.Pools) ?? new())
                {
                    [body.PoolId] = new(currentLength, newReservedOffset, DateTimeOffset.UtcNow),
                });
            try { await hwmStore.WriteAsync(newHwm, CancellationToken.None); }
            catch (Exception) { await tx.RollbackAsync(); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }

        // Step 7a: archive-confirmed reservation must also cover this lease.
        var covered = await reservations.EnsureCoveredAsync(conn, tx, body.PoolId, currentLength, offsetWithinLength, aheadMargin, CancellationToken.None);
        if (!covered)
        {
            await tx.RollbackAsync();
            return Results.Json(new { error = "reservation-pending" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Step 8-9: update position, insert the lease.
        await using (var updateCmd = new NpgsqlCommand(
            "UPDATE alloc.pools SET current_length = $1, next_offset = $2, updated_at = now() WHERE pool_id = $3", conn, tx))
        {
            updateCmd.Parameters.AddWithValue((short)currentLength);
            updateCmd.Parameters.AddWithValue(offsetWithinLength);
            updateCmd.Parameters.AddWithValue(body.PoolId);
            await updateCmd.ExecuteNonQueryAsync();
        }

        long leaseId;
        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO alloc.leases (request_id, pool_id, node_id, code_length, key_version, start_number, end_number, issued_at)
            VALUES ($1, $2, $3, $4, 1, $5, $6, now()) RETURNING lease_id
            """, conn, tx))
        {
            insertCmd.Parameters.AddWithValue(body.RequestId);
            insertCmd.Parameters.AddWithValue(body.PoolId);
            insertCmd.Parameters.AddWithValue(body.NodeId);
            insertCmd.Parameters.AddWithValue((short)currentLength);
            insertCmd.Parameters.AddWithValue(start);
            insertCmd.Parameters.AddWithValue(end);
            leaseId = (long)(await insertCmd.ExecuteScalarAsync())!;
        }

        await tx.CommitAsync();
        return Results.Ok(new LeaseResponse(leaseId, body.PoolId, currentLength, 1, start, end));
    }
    finally
    {
        gate.Release();
    }
});

// ---- Admin (spec §6.1) -----------------------------------------------------------------------------
app.MapPost("/v1/admin/init", async (InitRequest body) =>
{
    if (leasingState != LeasingState.NotInitialised)
        return Results.Conflict(new { error = "already-initialised-or-blocked", leasingState = leasingState.ToString() });
    if (!await sink.IsEmptyAsync(CancellationToken.None))
        return Results.Conflict(new { error = "sink-not-empty", detail = "A non-empty sink means codes may already have been issued; recover instead of init." });

    var installationId = Guid.NewGuid().ToString();
    await using var conn = await dataSource.OpenConnectionAsync();
    var (dbLength, dbOffset) = await StartupCheck.ReadPoolPositionAsync(conn, StartupCheck.PoolId, CancellationToken.None);

    var hwm = new HwmFile(1, installationId, body.KeyFingerprint, "initializing",
        new Dictionary<string, PoolHwm> { [StartupCheck.PoolId] = new(dbLength, dbOffset, DateTimeOffset.UtcNow) });
    await hwmStore.WriteAsync(hwm, CancellationToken.None);

    await sink.TryWriteManifestAsync(new InstallationManifest(installationId, body.KeyFingerprint, StartupCheck.PoolId, dbLength, DateTimeOffset.UtcNow), CancellationToken.None);
    await sink.TryWriteLengthMarkerAsync(new LengthMarker(installationId, body.KeyFingerprint, StartupCheck.PoolId, dbLength, DateTimeOffset.UtcNow), CancellationToken.None);

    await using (var insertCmd = new NpgsqlCommand(
        "INSERT INTO alloc.installation (installation_id, key_fingerprint, initialized_at) VALUES ($1, $2, now())", conn))
    {
        insertCmd.Parameters.AddWithValue(Guid.Parse(installationId));
        insertCmd.Parameters.AddWithValue(body.KeyFingerprint);
        await insertCmd.ExecuteNonQueryAsync();
    }

    await hwmStore.WriteAsync(hwm with { State = "active" }, CancellationToken.None);

    await using (var logCmd = new NpgsqlCommand(
        "INSERT INTO alloc.recovery_log (at, mode, details, operator) VALUES (now(), 'init', $1::jsonb, $2)", conn))
    {
        logCmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { installationId, body.KeyFingerprint }));
        logCmd.Parameters.AddWithValue(body.Operator);
        await logCmd.ExecuteNonQueryAsync();
    }

    leasingState = LeasingState.Ok;
    leasingReason = null;
    startup = startup with { State = LeasingState.Ok, InstallationId = installationId, KeyFingerprint = body.KeyFingerprint, Reason = null };
    return Results.Ok(new { installationId, body.KeyFingerprint });
});

app.MapPost("/v1/admin/recover", async (RecoverRequest body) =>
{
    RecoveryOutcome outcome = body.Mode switch
    {
        "reattach" => await recovery.ReattachAsync(body.Operator, CancellationToken.None),
        "trust-database" => await recovery.TrustDatabaseAsync(body.Operator, body.ConfirmDatabaseComplete ?? false, CancellationToken.None),
        "resume" => await recovery.ResumeAsync(body.Operator, app.Services.GetRequiredService<IArchiveReader>(), CancellationToken.None),
        "new-length" => await recovery.NewLengthAsync(body.Operator, CancellationToken.None),
        _ => new RecoveryOutcome(false, body.Mode, "Unknown recovery mode."),
    };

    if (outcome.Success)
    {
        // Re-evaluate the startup check now that recovery has written a fresh active HWM.
        startup = await StartupCheck.EvaluateAsync(dataSource, hwmStore, sink, CancellationToken.None);
        leasingState = startup.State;
        leasingReason = startup.Reason;
    }

    return outcome.Success ? Results.Ok(outcome) : Results.Json(outcome, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/v1/admin/pools/{poolId}/advance", async (string poolId, AdvanceRequest body) =>
{
    await using var conn = await dataSource.OpenConnectionAsync();
    var (currentLength, _) = await StartupCheck.ReadPoolPositionAsync(conn, poolId, CancellationToken.None);
    var toLength = body.ToLength ?? currentLength + 1;
    if (toLength <= currentLength || toLength > CodeLengths.MaxLength)
        return Results.BadRequest(new { error = "invalid-length" });

    var marker = new LengthMarker(startup.InstallationId ?? "", startup.KeyFingerprint ?? "", poolId, toLength, DateTimeOffset.UtcNow);
    if (!await sink.TryWriteLengthMarkerAsync(marker, CancellationToken.None))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    await using (var cmd = new NpgsqlCommand("UPDATE alloc.pools SET current_length = $1, next_offset = 0, updated_at = now() WHERE pool_id = $2", conn))
    {
        cmd.Parameters.AddWithValue((short)toLength);
        cmd.Parameters.AddWithValue(poolId);
        await cmd.ExecuteNonQueryAsync();
    }
    await using (var logCmd = new NpgsqlCommand("INSERT INTO alloc.recovery_log (at, mode, details, operator) VALUES (now(), 'advance', $1::jsonb, $2)", conn))
    {
        logCmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { poolId, toLength }));
        logCmd.Parameters.AddWithValue(body.Operator);
        await logCmd.ExecuteNonQueryAsync();
    }
    return Results.Ok(new { poolId, currentLength = toLength });
});

app.MapPut("/v1/admin/pools/{poolId}/ranges/{length:int}", async (string poolId, int length, RangeUpdateRequest body) =>
{
    await using var conn = await dataSource.OpenConnectionAsync();
    var cap = CodeLengths.GeneratorCap(length);
    if (body.RangeEnd is { } re && re > cap) return Results.BadRequest(new { error = "range-end-exceeds-generator-cap" });
    if (body.BlockSize is { } bs && (bs < 100 || bs > 1_000_000)) return Results.BadRequest(new { error = "invalid-block-size" });
    if (body.ReserveAheadBlocks is { } rab && (rab < 1 || rab > 100)) return Results.BadRequest(new { error = "invalid-reserve-ahead-blocks" });

    await using var cmd = new NpgsqlCommand(
        """
        UPDATE alloc.pool_ranges SET
            range_end = COALESCE($1, range_end),
            block_size = COALESCE($2, block_size),
            reserve_ahead_blocks = COALESCE($3, reserve_ahead_blocks)
        WHERE pool_id = $4 AND code_length = $5
        """, conn);
    cmd.Parameters.AddWithValue((object?)body.RangeEnd ?? DBNull.Value);
    cmd.Parameters.AddWithValue((object?)body.BlockSize ?? DBNull.Value);
    cmd.Parameters.AddWithValue((object?)body.ReserveAheadBlocks ?? DBNull.Value);
    cmd.Parameters.AddWithValue(poolId);
    cmd.Parameters.AddWithValue((short)length);
    var rows = await cmd.ExecuteNonQueryAsync();
    if (rows == 0) return Results.NotFound();

    await using var logCmd = new NpgsqlCommand("INSERT INTO alloc.recovery_log (at, mode, details, operator) VALUES (now(), 'ranges-update', $1::jsonb, $2)", conn);
    logCmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { poolId, length, body.RangeEnd, body.BlockSize, body.ReserveAheadBlocks }));
    logCmd.Parameters.AddWithValue(body.Operator);
    await logCmd.ExecuteNonQueryAsync();
    return Results.Ok();
});

app.Run();

static async Task<(long RangeStart, long RangeEnd, int BlockSize, int ReserveAheadBlocks)> ReadRangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string poolId, int length)
{
    await using var cmd = new NpgsqlCommand(
        "SELECT range_start, range_end, block_size, reserve_ahead_blocks FROM alloc.pool_ranges WHERE pool_id = $1 AND code_length = $2", conn, tx);
    cmd.Parameters.AddWithValue(poolId);
    cmd.Parameters.AddWithValue((short)length);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) throw new InvalidOperationException($"No seeded range for pool {poolId}, length {length}.");
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3));
}

internal sealed record LeaseRequest(Guid RequestId, string NodeId, string PoolId, string KeyFingerprint);
internal sealed record LeaseResponse(long LeaseId, string PoolId, int CodeLength, short KeyVersion, long StartNumber, long EndNumber);
internal sealed record InitRequest(string Operator, string KeyFingerprint);
internal sealed record RecoverRequest(string Operator, string Mode, bool? ConfirmDatabaseComplete);
internal sealed record AdvanceRequest(string Operator, int? Margin, int? ToLength);
internal sealed record RangeUpdateRequest(string Operator, long? RangeEnd, int? BlockSize, int? ReserveAheadBlocks);

public partial class Program; // for WebApplicationFactory-style test access
