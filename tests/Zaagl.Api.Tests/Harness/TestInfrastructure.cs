using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Zaagl.Api.Tests.Harness;

/// <summary>
/// The shared dev PostgreSQL (spec says tests should own their DB - QA-002 - but that harness does not exist
/// yet). Override with ZAAGL_TEST_PG. Every row this project creates carries a unique name; nothing here
/// deletes rows it did not create.
/// </summary>
public static class TestDb
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("ZAAGL_TEST_PG")
        ?? "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
}

/// <summary>
/// Wall-clock time plus a test-controlled offset. Replaces the app's TimeProvider so tests can move "now"
/// forward (link expiry) without freezing timers that background services rely on.
/// </summary>
public sealed class OffsetClock : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}

/// <summary>Unique, non-routable addresses so every test owns its own rate-limit partitions.</summary>
public static class TestIps
{
    /// <summary>A random IPv4 address in 198.18.0.0/15 (RFC 2544 benchmarking range).</summary>
    public static IPAddress RandomV4()
    {
        Span<byte> b = stackalloc byte[3];
        RandomNumberGenerator.Fill(b);
        return new IPAddress(new byte[] { 198, (byte)(18 + (b[0] & 1)), b[1], (byte)(1 + b[2] % 254) });
    }

    /// <summary>A random /24 in 198.18.0.0/15, as its 3 leading bytes.</summary>
    public static byte[] RandomV4Prefix24()
    {
        Span<byte> b = stackalloc byte[2];
        RandomNumberGenerator.Fill(b);
        return [198, (byte)(18 + (b[0] & 1)), b[1]];
    }

    /// <summary>A random /48 inside 2001:db8::/32 (documentation range), returned as its 6 leading bytes.</summary>
    public static byte[] RandomV6Prefix48()
    {
        var p = new byte[6];
        p[0] = 0x20; p[1] = 0x01; p[2] = 0x0d; p[3] = 0xb8;
        RandomNumberGenerator.Fill(p.AsSpan(4));
        return p;
    }

    /// <summary>IPv6 address: 48-bit prefix, then byte 6 (selects the /56), byte 7 (selects the /64), then a 64-bit host id.</summary>
    public static IPAddress V6(byte[] prefix48, byte b6, byte b7, ulong host)
    {
        var bytes = new byte[16];
        prefix48.CopyTo(bytes, 0);
        bytes[6] = b6;
        bytes[7] = b7;
        for (var i = 0; i < 8; i++) bytes[15 - i] = (byte)(host >> (8 * i));
        return new IPAddress(bytes);
    }

    public static string UniqueSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
}

/// <summary>
/// Test seam: sets HttpContext.Connection.RemoteIpAddress from the X-Test-Remote-Ip request header, i.e. it
/// plays the TCP peer. Registered as an IStartupFilter, so it runs before the app's own pipeline (before
/// UseForwardedHeaders), and it removes the header so the app never sees it. Nothing in src/ is touched.
/// </summary>
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMiddleware) =>
        {
            if (ctx.Request.Headers.TryGetValue(Header, out var value) && IPAddress.TryParse(value.ToString(), out var ip))
            {
                ctx.Connection.RemoteIpAddress = ip;
                ctx.Request.Headers.Remove(Header);
            }
            await nextMiddleware(ctx);
        });
        next(app);
    };
}

/// <summary>Adds the remote-IP seam header to every request from one client.</summary>
public sealed class RemoteIpHandler(IPAddress ip) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(RemoteIpStartupFilter.Header))
            request.Headers.Add(RemoteIpStartupFilter.Header, ip.ToString());
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// STUB for the code-issuing coordinator (spec §6.1), which is not running on the dev machine. Answers the
/// two calls the API makes: GET /v1/status (with the fingerprint of the test scramble key, so CreationGate
/// reaches Ok) and POST /v1/leases (a block at code length 10 starting at a random number below 1% of 62^10,
/// so generated codes cannot realistically collide with rows other agents create).
/// </summary>
public sealed class FakeCoordinatorHandler(string keyFingerprint) : HttpMessageHandler
{
    private int _leaseCalls;
    public int LeaseCalls => _leaseCalls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/v1/status")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { keyFingerprint, leasing = "ok", leasingReason = (string?)null, restoreDetectedAt = (DateTimeOffset?)null }),
            });
        }
        if (request.Method == HttpMethod.Post && path == "/v1/leases")
        {
            Interlocked.Increment(ref _leaseCalls);
            const long space = 8_392_993_658_683_402L; // just under 1% of 62^10
            var start = (long)(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) % (ulong)(space - 200_000));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    leaseId = 1L, poolId = "default", codeLength = 10, keyVersion = (short)1,
                    startNumber = start, endNumber = start + 100_000,
                }),
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
