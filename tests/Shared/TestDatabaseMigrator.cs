using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Zaagl.SharedKernel;

namespace Zaagl.Tests.Shared;

/// <summary>
/// Brings the test database (<see cref="TestDatabase"/>) to the current schema once per test process: every
/// module's EF Core migrations (the same <see cref="IModuleMigrator"/> list <c>Zaagl.Api migrate</c> runs, in
/// the same order) plus the click-event partitions the analytics hosted service would create. A session-level
/// advisory lock serialises test processes (dotnet test runs projects in parallel) that migrate the same DB.
/// Linked into the test projects that reference Zaagl.Api.
/// </summary>
public static class TestDatabaseMigrator
{
    private const long MigrationLockKey = 0x7A61_61676C_7465L; // "zaagl te(st)"

    private static readonly Lazy<string> Ready = new(Migrate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The test database's connection string, migrated. Use this from every test.</summary>
    public static string ConnectionString => Ready.Value;

    private static string Migrate()
    {
        var cs = TestDatabase.ConnectionString();

        using var lockConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        lockConnection.Open();
        using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationLockKey})", lockConnection))
            take.ExecuteNonQuery();
        try
        {
            using var factory = new MigrationFactory(cs);
            foreach (var migrator in factory.Services.GetServices<IModuleMigrator>())
                migrator.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

            using var partitions = new NpgsqlCommand("SELECT analytics.ensure_click_partitions(current_date, 3)", lockConnection);
            partitions.ExecuteNonQuery();
        }
        finally
        {
            using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({MigrationLockKey})", lockConnection);
            release.ExecuteNonQuery();
        }
        return cs;
    }

    /// <summary>The real Program's DI graph with every hosted service removed: used only to resolve the migrators.</summary>
    private sealed class MigrationFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["ConnectionStrings:Valkey"] = TestDatabase.Valkey,
                ["Database:MigrateOnStartup"] = "false",
                ["Jwt:Key"] = "migration-only-jwt-signing-key-0123456789abcdef-0123",
                ["ShortLinks:BaseUrl"] = "http://zaa.test",
                ["ShortLinks:ScrambleKey"] = "migration-only-scramble-key-0123456789abcdef-0123",
                ["GUEST_KEY"] = "migration-only-guest-key-0123456789abcdef-0123456789",
                ["Coordinator:BaseUrl"] = "http://coordinator.zaa.test",
                ["Coordinator:Token"] = "migration-only-coordinator-token",
                ["Hosts:Short"] = "zaa.test",
                ["Hosts:Api"] = "api.zaa.test",
                ["Frontend:BaseUrl"] = "http://app.zaa.test",
                ["Captcha:Enabled"] = "false",
                ["AdminBootstrap:Email"] = "",
                ["Serilog:MinimumLevel:Default"] = "Warning",
            };
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
            builder.ConfigureTestServices(services =>
            {
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(hosted);
            });
        }
    }
}
