using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel.Coordination;

namespace Zaagl.Links.Tests;

/// <summary>
/// Valkey (HybridCache L2) unavailable. Spec §7.0/§7.3: a cache failure after a commit never turns a
/// successful write into an error, redirects stay fast (database fallback), and invalidation still reaches L1
/// so a changed link is never served stale (R5).
/// </summary>
public sealed class ResilientDistributedCacheTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>An L2 whose behaviour the test switches: healthy (a dictionary), throwing, or hanging.</summary>
    private sealed class SwitchableL2 : IDistributedCache
    {
        public enum Mode { Healthy, Throw, Hang }
        public Mode Current = Mode.Healthy;
        public int Calls;
        public readonly Dictionary<string, byte[]> Store = new();

        private async Task<T> Do<T>(Func<T> healthy, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            switch (Current)
            {
                case Mode.Throw: throw new InvalidOperationException("L2 down");
                case Mode.Hang: await Task.Delay(Timeout.Infinite, ct); break;
            }
            lock (Store) return healthy();
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Do(() => Store.GetValueOrDefault(key), token);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Do(() => Store[key] = value, token);
        public Task RemoveAsync(string key, CancellationToken token = default) => Do(() => Store.Remove(key), token);
        public Task RefreshAsync(string key, CancellationToken token = default) => Do(() => true, token);
        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
    }

    private static ResilientDistributedCache Wrap(IDistributedCache inner, TimeProvider clock, int timeoutMs = 100) =>
        new(inner, Options.Create(new L2CacheOptions { OperationTimeoutMs = timeoutMs, FailuresBeforeBreak = 1, BreakSeconds = 30 }),
            clock, NullLogger<ResilientDistributedCache>.Instance);

    [Fact]
    public async Task A_hanging_L2_costs_one_short_timeout_then_is_skipped_without_waiting()
    {
        var inner = new SwitchableL2 { Current = SwitchableL2.Mode.Hang };
        var cache = Wrap(inner, new ManualClock(), timeoutMs: 100);

        var sw = Stopwatch.StartNew();
        Assert.Null(await cache.GetAsync("k"));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"first get took {sw.ElapsedMilliseconds} ms");
        Assert.True(cache.IsOpen);

        sw.Restart();
        for (var i = 0; i < 50; i++) Assert.Null(await cache.GetAsync("k"));
        await cache.SetAsync("k", [1], new DistributedCacheEntryOptions());
        await cache.RemoveAsync("k");
        Assert.True(sw.ElapsedMilliseconds < 100, $"open-circuit calls took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, inner.Calls); // only the first call reached L2
    }

    /// <summary>An L2 whose writes ignore cancellation and land only when the test releases them (a command that was
    /// already on the wire when the wrapper gave up).</summary>
    private sealed class LateWriteL2 : IDistributedCache
    {
        public readonly Dictionary<string, byte[]> Store = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? PendingWrite;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { lock (Store) return Task.FromResult(Store.GetValueOrDefault(key)); }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            PendingWrite = Task.Run(async () => { await Release.Task; lock (Store) Store[key] = value; });
            return Task.Delay(Timeout.Infinite, token); // the caller times out; the write itself still lands later
        }
        public Task RemoveAsync(string key, CancellationToken token = default) { lock (Store) Store.Remove(key); return Task.CompletedTask; }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_timed_out_write_that_lands_after_a_successful_delete_is_never_served()
    {
        var clock = new ManualClock();
        var inner = new LateWriteL2();
        var cache = new ResilientDistributedCache(inner,
            Options.Create(new L2CacheOptions { OperationTimeoutMs = 50, FailuresBeforeBreak = 5, BreakSeconds = 30, MaxEntryLifetimeSeconds = 300 }),
            clock, NullLogger<ResilientDistributedCache>.Instance);

        await cache.SetAsync("link:v2:abc", [1], new DistributedCacheEntryOptions()); // snapshot read before the change; times out
        Assert.Equal(1, cache.AbandonedWrites);
        await cache.RemoveAsync("link:v2:abc"); // the change's delete succeeds first
        inner.Release.SetResult();
        await inner.PendingWrite!; // ...then the stale write lands in L2
        Assert.True(inner.Store.ContainsKey("link:v2:abc"));

        Assert.Null(await cache.GetAsync("link:v2:abc")); // never served

        clock.Now += TimeSpan.FromSeconds(301); // the late copy has certainly expired in Valkey by now
        inner.Store.Remove("link:v2:abc");
        inner.Store["link:v2:abc"] = [2];
        Assert.Equal([2], await cache.GetAsync("link:v2:abc"));
        Assert.Equal(0, cache.AbandonedWrites);
    }

    [Fact]
    public async Task Operations_never_throw_when_L2_throws()
    {
        var cache = Wrap(new SwitchableL2 { Current = SwitchableL2.Mode.Throw }, new ManualClock());
        await cache.RemoveAsync("k");
        await cache.SetAsync("k", [1], new DistributedCacheEntryOptions());
        Assert.Null(await cache.GetAsync("k"));
    }

    [Fact]
    public async Task After_the_break_one_probe_closes_the_circuit_and_a_lost_delete_never_serves_the_stale_copy()
    {
        var clock = new ManualClock();
        var inner = new SwitchableL2();
        inner.Store["link:abc"] = [1, 2, 3]; // copy written before the outage
        var cache = Wrap(inner, clock);

        inner.Current = SwitchableL2.Mode.Throw;
        await cache.RemoveAsync("link:abc"); // the link changed; this delete is lost
        Assert.True(cache.IsOpen);
        Assert.Equal(1, cache.PendingRemovals);

        inner.Current = SwitchableL2.Mode.Healthy;
        clock.Now += TimeSpan.FromSeconds(31); // break over: next call is the probe

        Assert.Null(await cache.GetAsync("link:abc")); // stale copy is not served...
        Assert.False(inner.Store.ContainsKey("link:abc")); // ...and the lost delete was replayed
        Assert.Equal(0, cache.PendingRemovals);
        Assert.False(cache.IsOpen);

        inner.Store["other"] = [9];
        Assert.Equal([9], await cache.GetAsync("other")); // normal reads resume
    }

    [Fact]
    public async Task HybridCache_over_a_failing_L2_still_invalidates_L1_so_a_changed_entry_is_reloaded()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddSingleton<IDistributedCache>(Wrap(new SwitchableL2 { Current = SwitchableL2.Mode.Throw }, TimeProvider.System));
        sc.AddHybridCache();
        await using var sp = sc.BuildServiceProvider();
        var hybrid = sp.GetRequiredService<HybridCache>();

        var version = "enabled";
        async Task<string> Read() => await hybrid.GetOrCreateAsync("link:x", _ => ValueTask.FromResult(version));

        Assert.Equal("enabled", await Read());
        version = "disabled";
        Assert.Equal("enabled", await Read()); // L1 hit

        await hybrid.RemoveAsync("link:x"); // must not throw although L2 is down
        Assert.Equal("disabled", await Read()); // R5: the change is visible immediately
    }
}

/// <summary>The real app with Valkey down. <c>Unreachable</c>: the production wiring (Redis client + the
/// resilient wrapper) pointed at a closed port. <c>RawThrowing</c>: an L2 that throws on every call with no
/// wrapper at all, proving the post-commit guard in the Links module by itself.</summary>
public class L2OutageApiFactory(L2OutageApiFactory.Mode mode) : WebApplicationFactory<Program>
{
    public enum Mode { Unreachable, RawThrowing }

    public const string ApiHost = "api.zaa.test";
    public const string ShortHost = "zaa.test";
    private const string Fingerprint = "l2-outage-test-fingerprint";

    private sealed class ThrowingL2 : IDistributedCache
    {
        private static Exception Down() => new InvalidOperationException("Valkey unavailable (test double)");
        public byte[]? Get(string key) => throw Down();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Down();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Down();
        public void Refresh(string key) => throw Down();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Down();
        public void Remove(string key) => throw Down();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Down();
    }

    /// <summary>Status: key matches. Leases: a random 10-character block, so this test's generated numbers do
    /// not collide with other rows in the shared database (no custom slug: an alias freeze may be active there).</summary>
    private sealed class CoordinatorOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body = request.RequestUri!.AbsolutePath.EndsWith("/leases", StringComparison.Ordinal)
                ? new
                {
                    leaseId = 1L, poolId = "default", codeLength = 10, keyVersion = (short)1,
                    startNumber = Start, endNumber = Start + 1000,
                }
                : new { keyFingerprint = Fingerprint, leasing = "ok" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }

        private static readonly long Start =
            (long)(BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) % 700_000_000_000_000_000UL);
    }

    private sealed class StarterPlan : Zaagl.Modules.Billing.Contracts.IPlanProvider
    {
        public Task<Zaagl.Modules.Billing.Contracts.PlanLimits> GetLimitsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult(Zaagl.Modules.Billing.Contracts.PlanLimits.For(Zaagl.Modules.Billing.Contracts.PlanTier.Starter));
    }

    private sealed class SafeReputation : IUrlReputationChecker
    {
        public Task<ReputationResult> CheckAsync(string url, CancellationToken ct) => Task.FromResult(new ReputationResult(true));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Postgres"] = RealApiFactory.ConnectionString,
            // Nothing listens on 6390: every Redis call fails or times out.
            ["ConnectionStrings:Valkey"] = "127.0.0.1:6390,abortConnect=false,connectTimeout=5000",
            ["Database:MigrateOnStartup"] = "false",
            ["Jwt:Key"] = "links-tests-jwt-signing-key-0123456789abcdef-0123",
            ["ShortLinks:BaseUrl"] = "http://zaa.test",
            ["ShortLinks:ScrambleKey"] = "links-tests-scramble-key-0123456789abcdef-0123456789",
            ["GUEST_KEY"] = "links-tests-guest-key-0123456789abcdef-0123456789",
            ["Coordinator:BaseUrl"] = "http://coordinator.zaa.test",
            ["Hosts:Short"] = ShortHost,
            ["Hosts:Api"] = ApiHost,
            ["Frontend:BaseUrl"] = "http://app.zaa.test",
            ["AdminBootstrap:Email"] = "",
            ["Serilog:MinimumLevel:Default"] = "Error",
            ["Cache:L2:OperationTimeoutMs"] = "150",
            ["RateLimits:LinkWritePerMinute"] = "1000",
        };
        foreach (var (k, v) in settings) builder.UseSetting(k, v);

        builder.ConfigureTestServices(services =>
        {
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                services.Remove(hosted);

            var gate = new CreationGate(new HttpClient(new CoordinatorOk()) { BaseAddress = new Uri("http://coordinator.zaa.test") }, Fingerprint);
            gate.PollOnceAsync(default).GetAwaiter().GetResult();
            services.RemoveAll<CreationGate>();
            services.AddSingleton(gate);
            services.RemoveAll<Zaagl.SharedKernel.ShortCodes.CoordinatorLeaseClient>();
            services.AddSingleton(new Zaagl.SharedKernel.ShortCodes.CoordinatorLeaseClient(
                new HttpClient(new CoordinatorOk()) { BaseAddress = new Uri("http://coordinator.zaa.test") }, Fingerprint));
            services.RemoveAll<Zaagl.Modules.Billing.Contracts.IPlanProvider>();
            services.AddSingleton<Zaagl.Modules.Billing.Contracts.IPlanProvider, StarterPlan>();
            services.RemoveAll<IUrlReputationChecker>();
            services.AddSingleton<IUrlReputationChecker, SafeReputation>();

            if (mode == Mode.RawThrowing)
            {
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache, ThrowingL2>();
            }
        });
    }

    public HttpClient Client(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}/"), AllowAutoRedirect = false });
}

public abstract class L2OutageApiTestsBase(L2OutageApiFactory api) : IAsyncLifetime
{
    private User _user = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        _user = new User { Email = $"l2-{Guid.NewGuid():N}@example.test", DisplayName = "L2 outage", EmailConfirmed = true };
        db.Users.Add(_user);
        await db.SaveChangesAsync();
        _token = api.Services.GetRequiredService<TokenService>().IssueAccessToken(_user).Token;
    }

    public async Task DisposeAsync()
    {
        using var scope = api.Services.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
        await links.Links.Where(l => l.OwnerId == _user.Id).ExecuteDeleteAsync();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.Users.Where(u => u.Id == _user.Id).ExecuteDeleteAsync();
    }

    private async Task<(HttpResponseMessage Response, long Ms)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = api.Client(L2OutageApiFactory.ApiHost);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null) request.Content = JsonContent.Create(body);
        var sw = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        return (response, sw.ElapsedMilliseconds);
    }

    private async Task<(HttpStatusCode Status, long Ms)> RedirectAsync(string slug)
    {
        using var client = api.Client(L2OutageApiFactory.ShortHost);
        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync($"/{slug}");
        return (response.StatusCode, sw.ElapsedMilliseconds);
    }

    [Fact]
    public async Task Create_update_disable_and_delete_return_their_normal_status_and_redirects_stay_fast()
    {
        var (created, createMs) = await SendAsync(HttpMethod.Post, "/api/links", new LinkInput("https://example.com/one"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Assert.True(createMs < 2000, $"create took {createMs} ms");
        var link = (await created.Content.ReadFromJsonAsync<LinkDto>())!;
        var slug = link.Slug;

        // Redirect with L2 down: the first lookup may pay one L2 timeout (150 ms here), never Redis's 5 s.
        var first = await RedirectAsync(slug);
        Assert.Equal(HttpStatusCode.Found, first.Status);
        Assert.True(first.Ms < 1000, $"first redirect took {first.Ms} ms");
        var second = await RedirectAsync(slug);
        Assert.Equal(HttpStatusCode.Found, second.Status);
        Assert.True(second.Ms < 1000, $"second redirect took {second.Ms} ms");

        var (updated, _) = await SendAsync(HttpMethod.Put, $"/api/links/{link.Id}", new LinkInput("https://example.com/two"));
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        var (disabled, _) = await SendAsync(HttpMethod.Patch, $"/api/links/{link.Id}", new LinkFlagsInput(IsActive: false));
        Assert.True(disabled.StatusCode == HttpStatusCode.OK, await disabled.Content.ReadAsStringAsync());
        // R5: the disable is visible at once although the L2 delete failed (L1 was invalidated).
        Assert.Equal(HttpStatusCode.Gone, (await RedirectAsync(slug)).Status);

        var (deleted, _) = await SendAsync(HttpMethod.Delete, $"/api/links/{link.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }
}

public sealed class L2UnreachableApiTests(L2UnreachableApiTests.Factory api) : L2OutageApiTestsBase(api), IClassFixture<L2UnreachableApiTests.Factory>
{
    public sealed class Factory() : L2OutageApiFactory(Mode.Unreachable);
}

public sealed class L2ThrowingApiTests(L2ThrowingApiTests.Factory api) : L2OutageApiTestsBase(api), IClassFixture<L2ThrowingApiTests.Factory>
{
    public sealed class Factory() : L2OutageApiFactory(Mode.RawThrowing);
}
