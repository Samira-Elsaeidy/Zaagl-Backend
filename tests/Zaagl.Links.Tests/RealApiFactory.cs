using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Zaagl.Links.Tests;

/// <summary>
/// The real Zaagl.Api Program (every endpoint, the real authentication and authorization setup) for tests
/// that must see the app exactly as it is mapped. Every hosted service is removed, so nothing sweeps or
/// migrates the test database; only requests a test sends touch PostgreSQL (zaagl_test, tests/Shared/TestDatabase.cs). Valkey is
/// never contacted unless a request uses the cache.
/// </summary>
public sealed class RealApiFactory : WebApplicationFactory<Program>
{
    public const string ApiHost = "api.zaa.test";
    public static readonly string ConnectionString = Zaagl.Tests.Shared.TestDatabaseMigrator.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Postgres"] = ConnectionString,
            ["ConnectionStrings:Valkey"] = Zaagl.Tests.Shared.TestDatabase.Valkey,
            ["Database:MigrateOnStartup"] = "false",
            ["Jwt:Key"] = "links-tests-jwt-signing-key-0123456789abcdef-0123",
            ["ShortLinks:BaseUrl"] = "http://zaa.test",
            ["ShortLinks:ScrambleKey"] = "links-tests-scramble-key-0123456789abcdef-0123456789",
            ["GUEST_KEY"] = "links-tests-guest-key-0123456789abcdef-0123456789",
            ["Coordinator:BaseUrl"] = "http://coordinator.zaa.test",
            ["Coordinator:Token"] = "links-tests-coordinator-token",
            ["Hosts:Short"] = "zaa.test",
            ["Hosts:Api"] = ApiHost,
            ["Frontend:BaseUrl"] = "http://app.zaa.test",
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

    public HttpClient ApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{ApiHost}/"), AllowAutoRedirect = false });
}
