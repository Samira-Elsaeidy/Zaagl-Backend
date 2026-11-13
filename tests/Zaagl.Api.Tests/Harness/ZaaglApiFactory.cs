using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Api.Tests.Harness;

/// <summary>
/// QA-006: the API hosted in-process on its real Program, real modules, real PostgreSQL.
///
/// Stubs and overrides (each one is unavoidable on the dev machine, and documented in
/// docs/phase1-implementation/qa/stage1-baseline.md):
///  1. Coordinator: the named HttpClients "CoordinatorLeaseClient" and "CreationGate" get
///     <see cref="FakeCoordinatorHandler"/> as primary handler (no coordinator runs locally).
///  2. Valkey: IDistributedCache is replaced by the in-memory distributed cache (no Valkey locally), so
///     HybridCache runs L1 + in-memory L2. The /health/ready redis check is not exercised.
///  3. TimeProvider: replaced by <see cref="OffsetClock"/> so expiry can be tested without sleeping.
///  4. Hosted services: only CreationGatePoller, LinkCacheInvalidationListener and ClickWriterService run.
///     Every other one is removed because it sweeps the whole shared database (purge, safety re-check,
///     unconfirmed-account cleanup, email dispatch/digest, partition DDL, guest-quota cleanup, admin
///     bootstrap, migrations) and would mutate rows other agents' tests own.
///  5. Remote IP: <see cref="RemoteIpStartupFilter"/> plays the TCP peer address.
///  6. Captcha disabled (Captcha:Enabled=false -> NoOpCaptchaVerifier); WebRisk/Stripe/GeoIP/SMTP left
///     unconfigured (the product already skips them when unset).
/// Environment is "Testing" (not Development): no OpenAPI/Scalar routes, no dev-only token echo.
/// </summary>
public class ZaaglApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ShortHost = "zaa.test";
    public const string ApiHost = "api.zaa.test";
    public const string WebBase = "http://app.zaa.test";
    public const string AdminNetwork = "10.200.0.0/16";
    public const string ScrambleKey = "qa-test-scramble-key-0123456789abcdef-0123456789";
    public const string JwtKey = "qa-test-jwt-signing-key-0123456789abcdef-0123456789";
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly HashSet<string> KeptHostedServices =
    [
        "CreationGatePoller",
        "LinkCacheInvalidationListener",
        "ClickWriterService",
    ];

    public OffsetClock Clock { get; } = new();

    /// <summary>Extra settings a subclass can add (e.g. Network:KnownProxies for BE-030 tests).</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> ExtraSettings() => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Postgres"] = TestDb.ConnectionString,
            ["ConnectionStrings:Valkey"] = Zaagl.Tests.Shared.TestDatabase.Valkey,
            ["Database:MigrateOnStartup"] = "false",
            ["Jwt:Key"] = JwtKey,
            ["ShortLinks:BaseUrl"] = $"http://{ShortHost}",
            ["ShortLinks:ScrambleKey"] = ScrambleKey,
            ["GUEST_KEY"] = "qa-test-guest-key-0123456789abcdef-0123456789",
            ["Coordinator:BaseUrl"] = "http://coordinator.zaa.test",
            ["Coordinator:Token"] = "qa-test-coordinator-token",
            ["Hosts:Short"] = ShortHost,
            ["Hosts:Api"] = ApiHost,
            ["Frontend:BaseUrl"] = WebBase,
            ["Captcha:Enabled"] = "false",
            ["Admin:AllowedNetworks:0"] = AdminNetwork,
            ["AdminBootstrap:Email"] = "",
            ["Resolution:VisitorSalt"] = "qa-test-visitor-salt",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };
        foreach (var (k, v) in ExtraSettings()) settings[k] = v;
        // UseSetting values are visible to Program's top-level configuration reads (before Build()).
        foreach (var (k, v) in settings) builder.UseSetting(k, v);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteIpStartupFilter>();

            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            var fingerprint = KeyFingerprint.Compute(ScrambleKey);
            services.AddHttpClient(nameof(CoordinatorLeaseClient))
                .ConfigurePrimaryHttpMessageHandler(() => new FakeCoordinatorHandler(fingerprint));
            services.AddHttpClient(nameof(CreationGate))
                .ConfigurePrimaryHttpMessageHandler(() => new FakeCoordinatorHandler(fingerprint));

            var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
            foreach (var d in hosted)
            {
                var name = d.ImplementationType?.Name ?? d.ImplementationInstance?.GetType().Name ?? "";
                if (!KeptHostedServices.Contains(name)) services.Remove(d);
            }
        });
    }

    public async Task InitializeAsync()
    {
        _ = Server; // build and start the host
        var gate = Services.GetRequiredService<CreationGate>();
        for (var i = 0; i < 50 && !gate.CanCreate; i++)
            await gate.PollOnceAsync(CancellationToken.None);
        if (!gate.CanCreate) throw new InvalidOperationException($"CreationGate did not reach Ok against the fake coordinator: {gate.State}");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await CleanUpOutboxAsync();
        await base.DisposeAsync();
    }

    /// <summary>Registering a user queues a confirm-email row that no test dispatches (the email dispatcher is one of
    /// the hosted services removed above). Left pending, those rows are picked up by the next EmailJobsTests run, which
    /// then sends, fails or suppresses them and can miss its own row in the batch. Every address this harness creates
    /// matches <c>qa-api-*@example.test</c>; its outbox rows are removed with the factory.</summary>
    private static async Task CleanUpOutboxAsync()
    {
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(TestDb.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM identity.email_outbox o
                WHERE o.recipient_email LIKE 'qa-api-%@example.test'
                   OR o.user_id IN (SELECT id FROM identity.users WHERE email LIKE 'qa-api-%@example.test')
                """;
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Test cleanup of identity.email_outbox failed (ignored): {ex.Message}");
        }
    }

    // ---- Clients ------------------------------------------------------------------------

    /// <summary>Client whose requests carry Host = the short host and come from <paramref name="ip"/>.
    /// No redirect following, no cookie container (so Set-Cookie is observable on the raw response).</summary>
    public HttpClient ShortClient(IPAddress? ip = null) => HostClient(ShortHost, ip);

    public HttpClient ApiClient(IPAddress? ip = null, string? bearer = null, string? apiKey = null)
    {
        var client = HostClient(ApiHost, ip);
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (apiKey is not null) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public HttpClient HostClient(string host, IPAddress? ip = null)
    {
        var client = CreateDefaultClient(new Uri($"http://{host}/"), new RemoteIpHandler(ip ?? TestIps.RandomV4()));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    public TestPrincipals Principals => new(this);
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ZaaglApiFactory>
{
    public const string Name = "Zaagl API (WebApplicationFactory)";
}
