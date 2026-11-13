using Npgsql;

namespace Zaagl.Tests.Shared;

/// <summary>
/// The PostgreSQL database the test projects use. Tests NEVER touch the developer database (<c>zaagl</c>):
/// a test run once truncated <c>alloc.*</c> and reset the short-code allocator of the running dev stack.
///
/// Default: <c>zaagl_test</c> on the dev server (localhost:5433), created automatically when missing. A test
/// project may pick its own database name (the coordinator tests wipe <c>alloc.*</c>, so they get
/// <c>zaagl_test_coordinator</c>). <c>ZAAGL_TEST_PG</c> overrides the whole connection string (CI); the
/// database it names must not be one of <see cref="ForbiddenDatabases"/>, or every test fails fast.
/// Linked into each test project (see the csproj files), so it has no dependency beyond Npgsql.
/// </summary>
public static class TestDatabase
{
    public const string DefaultName = "zaagl_test";

    /// <summary>Databases a test must never connect to: the dev stack's and the server's maintenance ones.</summary>
    public static readonly IReadOnlySet<string> ForbiddenDatabases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zaagl", "postgres", "template0", "template1" };

    private static readonly object Gate = new();
    private static readonly HashSet<string> Created = new(StringComparer.Ordinal);

    /// <summary>The connection string for <paramref name="databaseName"/> (or the ZAAGL_TEST_PG one); the
    /// database is created if it does not exist. Throws if it would point at the dev database.</summary>
    public static string ConnectionString(string databaseName = DefaultName)
    {
        var raw = Environment.GetEnvironmentVariable("ZAAGL_TEST_PG");
        var builder = raw is null
            ? new NpgsqlConnectionStringBuilder
            {
                Host = "localhost",
                Port = 5433,
                Database = databaseName,
                Username = "zaagl",
                Password = "zaagl_dev",
                // The dev server allows 100 connections and the dev stack holds ~15. Every WebApplicationFactory host
                // builds its own Npgsql data sources (LinksModule registers instances the container never disposes), so their
                // idle connections used to pile up across a suite run until PostgreSQL answered 53300 "too many clients"
                // (and random 500s). Small pools whose idle connections are pruned within seconds keep the total bounded.
                MaxPoolSize = 20,
                ConnectionIdleLifetime = 5,
                ConnectionPruningInterval = 1,
            }
            : new NpgsqlConnectionStringBuilder(raw);

        if (string.IsNullOrWhiteSpace(builder.Database) || ForbiddenDatabases.Contains(builder.Database))
            throw new InvalidOperationException(
                $"Refusing to run tests against database '{builder.Database}'. Tests use their own database " +
                $"(default '{DefaultName}'); set ZAAGL_TEST_PG to a dedicated test database.");

        var cs = builder.ConnectionString;
        EnsureExists(builder);
        return cs;
    }

    private static void EnsureExists(NpgsqlConnectionStringBuilder target)
    {
        lock (Gate)
        {
            if (Created.Contains(target.ConnectionString)) return;

            var admin = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres", Pooling = false };
            using var connection = new NpgsqlConnection(admin.ConnectionString);
            connection.Open();
            using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection))
            {
                exists.Parameters.AddWithValue(target.Database!);
                if (exists.ExecuteScalar() is null)
                {
                    // Identifier comes from our own constant or ZAAGL_TEST_PG; quote it anyway.
                    var quoted = "\"" + target.Database!.Replace("\"", "\"\"") + "\"";
                    using var create = new NpgsqlCommand($"CREATE DATABASE {quoted}", connection);
                    try { create.ExecuteNonQuery(); }
                    catch (PostgresException ex) when (ex.SqlState is "42P04" or "23505") { /* another test process won the race */ }
                }
            }
            Created.Add(target.ConnectionString);
        }
    }

    /// <summary>Valkey for tests: database 15, never the dev stack's database 0.</summary>
    public const string Valkey = "localhost:6379,abortConnect=false,defaultDatabase=15";
}
