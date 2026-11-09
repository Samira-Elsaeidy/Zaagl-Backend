using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Zaagl.SharedKernel.Jobs;

/// <summary>Task row status (ARC-012 §3).</summary>
public static class QueuedTaskStatus
{
    public const short Pending = 0;
    public const short Running = 1;
    public const short Done = 2;
    public const short Dead = 3;
}

/// <summary>
/// One row of a module's <c>&lt;schema&gt;.tasks</c> table (ARC-012 §3). Each owning module maps it in its own
/// DbContext with <see cref="TaskTableModel.MapTaskTable"/> and owns the migration. The payload holds ids only:
/// never a link key, password, grant or ticket (MUST NOT 7).
/// </summary>
public sealed class QueuedTask
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public short Status { get; set; }
    public DateTime RunAfter { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class TaskTableModel
{
    /// <summary>Maps <see cref="QueuedTask"/> to <c>tasks</c> in the context's default schema, with the open-task
    /// dedupe index and the due index of ARC-012 §3 (snake_case columns come from the naming convention).</summary>
    public static ModelBuilder MapTaskTable(this ModelBuilder b)
    {
        b.Entity<QueuedTask>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Kind).IsRequired();
            e.Property(x => x.DedupeKey).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Status).HasDefaultValue(QueuedTaskStatus.Pending);
            e.Property(x => x.RunAfter).HasDefaultValueSql("now()");
            e.Property(x => x.Attempts).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.DedupeKey).IsUnique().HasDatabaseName("tasks_dedupe_open").HasFilter("status IN (0, 1)");
            e.HasIndex(x => x.RunAfter).HasDatabaseName("tasks_due").HasFilter("status IN (0, 1)");
        });
        return b;
    }
}

/// <summary>A claimed task handed to a handler.</summary>
public sealed record ClaimedTask(long Id, string Kind, string DedupeKey, string Payload, int Attempts, int MaxAttempts);

/// <summary>
/// ARC-012 §3 operations on <c>&lt;schema&gt;.tasks</c>, as parameterised ADO.NET commands on the context's own
/// connection (never <c>Database.SqlQuery</c>). All times come from the caller's <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class TaskQueue
{
    private readonly string _table;

    public TaskQueue(string schema)
    {
        if (!Identifier().IsMatch(schema)) throw new ArgumentException("Invalid schema name.", nameof(schema));
        Schema = schema;
        _table = $"{schema}.tasks";
    }

    public string Schema { get; }

    /// <summary>
    /// Enqueues in the caller's current transaction (if any), so the task commits or rolls back with the state change
    /// that caused it. An open task (pending or running) with the same <paramref name="dedupeKey"/> wins: returns null.
    /// </summary>
    public async Task<long?> EnqueueAsync(
        DbContext db, string kind, string dedupeKey, string payloadJson, int maxAttempts, DateTime runAfterUtc,
        DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        return await WithCommandAsync(db, async cmd =>
        {
            cmd.CommandText = $"""
                INSERT INTO {_table} (kind, dedupe_key, payload, status, run_after, attempts, max_attempts, created_at)
                VALUES (@kind, @dedupe, CAST(@payload AS jsonb), 0, @run_after, 0, @max, @now)
                ON CONFLICT (dedupe_key) WHERE status IN (0, 1) DO NOTHING
                RETURNING id
                """;
            Add(cmd, "kind", kind);
            Add(cmd, "dedupe", dedupeKey);
            Add(cmd, "payload", payloadJson);
            Add(cmd, "run_after", runAfterUtc);
            Add(cmd, "max", maxAttempts);
            Add(cmd, "now", nowUtc);
            var id = await cmd.ExecuteScalarAsync(ct);
            return id is long l ? l : (long?)null;
        }, ct);
    }

    /// <summary>Claims up to <paramref name="batch"/> due tasks of <paramref name="kinds"/> (pending and due, or
    /// running with an expired lease) with <c>FOR UPDATE SKIP LOCKED</c>, so concurrent workers never share one.</summary>
    public async Task<IReadOnlyList<ClaimedTask>> ClaimAsync(
        DbContext db, IReadOnlyCollection<string> kinds, int batch, TimeSpan lease, string instance, DateTime nowUtc,
        CancellationToken ct)
    {
        if (kinds.Count == 0 || batch <= 0) return [];
        return await WithCommandAsync(db, async cmd =>
        {
            cmd.CommandText = $"""
                WITH due AS (
                    SELECT id FROM {_table}
                    WHERE kind = ANY(@kinds)
                      AND ((status = 0 AND run_after <= @now) OR (status = 1 AND locked_until < @now))
                    ORDER BY run_after, id
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
                UPDATE {_table} t
                SET status = 1, attempts = t.attempts + 1, locked_until = @lease_until, locked_by = @instance
                FROM due WHERE t.id = due.id
                RETURNING t.id, t.kind, t.dedupe_key, t.payload::text, t.attempts, t.max_attempts
                """;
            Add(cmd, "kinds", kinds.ToArray());
            Add(cmd, "now", nowUtc);
            Add(cmd, "batch", batch);
            Add(cmd, "lease_until", nowUtc + lease);
            Add(cmd, "instance", instance);
            var claimed = new List<ClaimedTask>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                claimed.Add(new ClaimedTask(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
            return (IReadOnlyList<ClaimedTask>)claimed.OrderBy(t => t.Id).ToList();
        }, ct);
    }

    /// <summary>Marks done. Only the current lease holder of this very claim can: the row must still be running,
    /// locked by <paramref name="instance"/> AND at the claim's attempt number (Head round 3 note 4), so a stale run
    /// whose lease expired and was re-claimed (by another worker or by this same process) never completes the new
    /// claim. Returns false when this claim no longer holds the task.</summary>
    public Task<bool> CompleteAsync(DbContext db, ClaimedTask task, string instance, DateTime nowUtc, CancellationToken ct) =>
        WithCommandAsync(db, async cmd =>
        {
            cmd.CommandText = $"""
                UPDATE {_table} SET status = 2, completed_at = @now, locked_until = NULL
                WHERE id = @id AND locked_by = @instance AND attempts = @attempts AND status = 1
                """;
            Add(cmd, "now", nowUtc);
            AddClaim(cmd, task, instance);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    /// <summary>Records a failure: back to pending at <paramref name="retryAtUtc"/>, or dead once attempts reached
    /// max_attempts. Returns the new status, or null when this worker no longer holds the lease.</summary>
    public Task<short?> FailAsync(DbContext db, ClaimedTask task, string instance, string error, DateTime retryAtUtc, CancellationToken ct) =>
        WithCommandAsync(db, async cmd =>
        {
            cmd.CommandText = $"""
                UPDATE {_table}
                SET status = CASE WHEN attempts >= max_attempts THEN 3 ELSE 0 END,
                    run_after = @retry_at, locked_until = NULL, last_error = @error
                WHERE id = @id AND locked_by = @instance AND attempts = @attempts AND status = 1
                RETURNING status
                """;
            Add(cmd, "retry_at", retryAtUtc);
            Add(cmd, "error", error.Length > 2000 ? error[..2000] : error);
            AddClaim(cmd, task, instance);
            return await cmd.ExecuteScalarAsync(ct) is short s ? s : (short?)null;
        }, ct);

    /// <summary>Extends the lease of a long-running task (call every lease/3). Fenced like <see cref="CompleteAsync"/>.</summary>
    public Task<bool> ExtendLeaseAsync(DbContext db, ClaimedTask task, string instance, DateTime leaseUntilUtc, CancellationToken ct) =>
        WithCommandAsync(db, async cmd =>
        {
            cmd.CommandText = $"UPDATE {_table} SET locked_until = @until WHERE id = @id AND locked_by = @instance AND attempts = @attempts AND status = 1";
            Add(cmd, "until", leaseUntilUtc);
            AddClaim(cmd, task, instance);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    /// <summary>Deletes done rows completed before <paramref name="beforeUtc"/>, at most 1,000 per statement.</summary>
    public async Task<int> DeleteDoneAsync(DbContext db, DateTime beforeUtc, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var n = await WithCommandAsync(db, async cmd =>
            {
                cmd.CommandText = $"""
                    DELETE FROM {_table} WHERE id IN (
                        SELECT id FROM {_table} WHERE status = 2 AND completed_at < @before LIMIT 1000)
                    """;
                Add(cmd, "before", beforeUtc);
                return await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            total += n;
            if (n < 1000) return total;
        }
    }

    private static async Task<T> WithCommandAsync<T>(DbContext db, Func<DbCommand, Task<T>> run, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            opened = true;
        }
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return await run(cmd);
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    private static void AddClaim(DbCommand cmd, ClaimedTask task, string instance)
    {
        Add(cmd, "id", task.Id);
        Add(cmd, "instance", instance);
        Add(cmd, "attempts", task.Attempts);
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    [GeneratedRegex(@"\A[a-z_][a-z0-9_]{0,62}\z")]
    private static partial Regex Identifier();
}
