using Npgsql;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>
/// A session-level PostgreSQL advisory lock on its own connection, so a long upload PATCH or a multi-module <c>/complete</c>
/// serialises per upload session without holding a transaction open (ARC-009 4.3, section 6). The lock dies with the
/// connection, so a killed process never leaves one behind.
/// </summary>
public sealed class AdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;

    private AdvisoryLock(NpgsqlConnection connection, long key)
    {
        _connection = connection;
        _key = key;
    }

    /// <summary>A stable 64-bit key for one upload session.</summary>
    public static long KeyFor(Guid sessionId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("zaagl.upload:" + sessionId.ToString("D")));
        return BitConverter.ToInt64(hash, 0);
    }

    /// <summary>Tries for at most <paramref name="wait"/> (zero: once). Null when another holder has it.</summary>
    public static async Task<AdvisoryLock?> TryAcquireAsync(string connectionString, long key, TimeSpan wait, TimeProvider time, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var deadline = time.GetUtcNow() + wait;
            while (true)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_try_advisory_lock(@key)";
                command.Parameters.AddWithValue("key", key);
                if (await command.ExecuteScalarAsync(ct) is true) return new AdvisoryLock(connection, key);
                if (time.GetUtcNow() >= deadline) break;
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            command.Parameters.AddWithValue("key", _key);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The connection is closed below; PostgreSQL releases the lock with it.
        }
        await _connection.DisposeAsync();
    }
}
