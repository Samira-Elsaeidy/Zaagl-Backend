using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>
/// QA-011: client-IP chain (spec §7.13) and redirect rate limits (spec §7.3). The oracle limiter for the
/// client-IP tests is the existing "unlock" policy (10 per minute per IP partition and link), because it is
/// cheap and already in place. Tests tagged Defect=F-02 / F-03 are expected to FAIL until BE-030 / BE-031.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "QA-011")]
public sealed class RateLimitAndClientIpTests(ZaaglApiFactory api)
{
    private TestPrincipals P => api.Principals;

    private async Task<LinkDto> PasswordLinkAsync()
    {
        var owner = await P.CreateUserAsync();
        return await P.CreateLinkAsync(owner, new LinkInput($"https://example.com/qa/rl/{TestIps.UniqueSuffix()}",
            Policy: new PolicyInput(Password: "rate limit oracle")));
    }

    private static HttpRequestMessage Unlock(string slug, IPAddress remote, string? xff = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/unlock/{slug}") { Content = JsonContent.Create(new { password = "wrong" }) };
        request.Headers.Add(RemoteIpStartupFilter.Header, remote.ToString());
        if (xff is not null) request.Headers.Add("X-Forwarded-For", xff);
        return request;
    }

    private static string RandomMissCode()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return "qm" + new string(Enumerable.Range(0, 10).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    private static HttpRequestMessage Get(string path, IPAddress remote, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add(RemoteIpStartupFilter.Header, remote.ToString());
        return request;
    }

    // ---- (control) the oracle limiter works ----------------------------------------------------

    [Fact]
    public async Task Control_eleventh_unlock_from_one_address_is_429()
    {
        var link = await PasswordLinkAsync();
        using var client = api.ApiClient();
        var remote = TestIps.RandomV4();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) statuses.Add((await client.SendAsync(Unlock(link.Slug, remote))).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    // ---- (a) forged X-Forwarded-For from an untrusted peer -------------------------------------------

    [Fact]
    [Trait("Defect", "F-02")]
    [Trait("Defect", "BE-030")]
    public async Task Forged_x_forwarded_for_from_an_untrusted_peer_does_not_change_its_partition()
    {
        var link = await PasswordLinkAsync();
        using var client = api.ApiClient();
        var remote = TestIps.RandomV4(); // a direct internet client, not a configured proxy
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await client.SendAsync(Unlock(link.Slug, remote, xff: TestIps.RandomV4().ToString()))).StatusCode);

        Assert.True(statuses[10] == HttpStatusCode.TooManyRequests,
            $"11 unlocks from {remote} with a different forged X-Forwarded-For each: [{string.Join(",", statuses.Select(s => (int)s))}]; " +
            "the 11th must be 429 because the partition is the real peer address (spec §7.13).");
    }

    [Fact]
    [Trait("Defect", "F-02")]
    [Trait("Defect", "BE-030")]
    public async Task Forged_x_forwarded_for_cannot_make_two_peers_share_a_partition()
    {
        // The reverse attack: frame a victim by sending its address in X-Forwarded-For.
        var link = await PasswordLinkAsync();
        using var client = api.ApiClient();
        var victim = TestIps.RandomV4();
        for (var i = 0; i < 10; i++)
            await client.SendAsync(Unlock(link.Slug, TestIps.RandomV4(), xff: victim.ToString()));

        var victimsOwn = await client.SendAsync(Unlock(link.Slug, victim));
        Assert.True(victimsOwn.StatusCode != HttpStatusCode.TooManyRequests,
            "Ten attackers sending X-Forwarded-For: <victim> used up the victim's unlock budget.");
    }

    // ---- (b) IPv6 /64 partitioning ---------------------------------------------------------------------

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Addresses_rotating_inside_one_ipv6_64_share_one_unlock_partition()
    {
        var link = await PasswordLinkAsync();
        using var client = api.ApiClient();
        var prefix = TestIps.RandomV6Prefix48();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await client.SendAsync(Unlock(link.Slug, TestIps.V6(prefix, 1, 1, (ulong)(i + 1) * 0x1_0000_0001UL)))).StatusCode);

        Assert.True(statuses[10] == HttpStatusCode.TooManyRequests,
            $"11 unlocks from 11 addresses in one /64: [{string.Join(",", statuses.Select(s => (int)s))}]; spec §7.3 partitions IPv6 by /64.");
    }

    [Fact]
    public async Task Different_ipv6_64s_get_separate_unlock_partitions()
    {
        var link = await PasswordLinkAsync();
        using var client = api.ApiClient();
        var prefix = TestIps.RandomV6Prefix48();
        for (var i = 0; i < 10; i++) await client.SendAsync(Unlock(link.Slug, TestIps.V6(prefix, 2, 1, (ulong)i + 1)));
        var neighbour = await client.SendAsync(Unlock(link.Slug, TestIps.V6(prefix, 2, 2, 1)));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour.StatusCode);
    }

    // ---- (c) redirect route limits, spec §7.3 --------------------------------------------------------------

    /// <summary>
    /// A burst must fit inside one limiter window. BE-031's limiter (and any fixed-window limiter aligned to the
    /// clock minute) resets at :00, so wait while the app clock is in the last 15 s of a minute. Bursts take
    /// under 2 s. Without this, a burst straddling the boundary passes for the wrong reason (seen once in run 2).
    /// </summary>
    private async Task AlignToWindowAsync()
    {
        while (api.Clock.GetUtcNow().Second >= 45)
            await Task.Delay(250);
    }

    private async Task<List<HttpStatusCode>> BurstAsync(IEnumerable<(string Path, IPAddress Ip, HttpMethod Method)> requests)
    {
        await AlignToWindowAsync();
        using var client = api.ShortClient();
        var statuses = new List<HttpStatusCode>();
        foreach (var (path, ip, method) in requests)
            statuses.Add((await client.SendAsync(Get(path, ip, method))).StatusCode);
        return statuses;
    }

    private static string Summary(List<HttpStatusCode> s) =>
        string.Join(", ", s.GroupBy(x => (int)x).Select(g => $"{g.Key}x{g.Count()}")) + $"; last={(int)s[^1]}";

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Redirects_are_limited_to_300_per_minute_per_partition()
    {
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner);
        var ip = TestIps.RandomV4();
        var statuses = await BurstAsync(Enumerable.Range(0, 301).Select(_ => ($"/{link.Slug}", ip, HttpMethod.Get)));

        Assert.All(statuses.Take(300), s => Assert.Equal(HttpStatusCode.Redirect, s));
        Assert.True(statuses[300] == HttpStatusCode.TooManyRequests, $"301 hits from one address: {Summary(statuses)}");
    }

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Misses_are_limited_to_30_per_minute_per_ipv4_address()
    {
        var ip = TestIps.RandomV4();
        var statuses = await BurstAsync(Enumerable.Range(0, 31).Select(_ => ($"/{RandomMissCode()}", ip, HttpMethod.Get)));
        Assert.All(statuses.Take(30), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.True(statuses[30] == HttpStatusCode.TooManyRequests, $"31 misses from one IPv4: {Summary(statuses)}");
    }

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Misses_are_limited_to_30_per_minute_per_ipv6_64()
    {
        var prefix = TestIps.RandomV6Prefix48();
        var statuses = await BurstAsync(Enumerable.Range(0, 31)
            .Select(i => ($"/{RandomMissCode()}", TestIps.V6(prefix, 3, 3, (ulong)i + 1), HttpMethod.Get)));
        Assert.All(statuses.Take(30), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.True(statuses[30] == HttpStatusCode.TooManyRequests, $"31 misses from 31 addresses in one /64: {Summary(statuses)}");
    }

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Misses_are_limited_to_120_per_minute_per_ipv6_56()
    {
        var prefix = TestIps.RandomV6Prefix48();
        // One miss from each of 121 different /64s inside one /56 (byte 6 fixed, byte 7 varies).
        var statuses = await BurstAsync(Enumerable.Range(0, 121)
            .Select(i => ($"/{RandomMissCode()}", TestIps.V6(prefix, 4, (byte)i, 1), HttpMethod.Get)));
        Assert.All(statuses.Take(120), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.True(statuses[120] == HttpStatusCode.TooManyRequests, $"121 misses from 121 /64s in one /56: {Summary(statuses)}");
    }

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Misses_are_limited_to_300_per_minute_per_ipv6_48()
    {
        var prefix = TestIps.RandomV6Prefix48();
        // 301 misses, each from its own /64, spread over 256 /56s, so no /64 or /56 limit is reached.
        var statuses = await BurstAsync(Enumerable.Range(0, 301)
            .Select(i => ($"/{RandomMissCode()}", TestIps.V6(prefix, (byte)(i % 256), (byte)(i / 256), 1), HttpMethod.Get)));
        Assert.All(statuses.Take(300), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.True(statuses[300] == HttpStatusCode.TooManyRequests, $"301 misses from 301 /64s in one /48: {Summary(statuses)}");
    }

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Misses_are_limited_to_300_per_minute_per_ipv4_24()
    {
        var p = TestIps.RandomV4Prefix24();
        // 301 misses over the 254 host addresses of one /24: at most 2 per address, so the per-address limit is not reached.
        var statuses = await BurstAsync(Enumerable.Range(0, 301)
            .Select(i => ($"/{RandomMissCode()}", new IPAddress(new[] { p[0], p[1], p[2], (byte)(1 + i % 254) }), HttpMethod.Get)));
        Assert.All(statuses.Take(300), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.True(statuses[300] == HttpStatusCode.TooManyRequests, $"301 misses from one IPv4 /24: {Summary(statuses)}");
    }

    [Fact]
    [Trait("Defect", "F-03")]
    [Trait("Defect", "BE-031")]
    public async Task Head_misses_count_like_get_misses()
    {
        var ip = TestIps.RandomV4();
        var requests = Enumerable.Range(0, 30).Select(_ => ($"/{RandomMissCode()}", ip, HttpMethod.Head))
            .Append(($"/{RandomMissCode()}", ip, HttpMethod.Get));
        var statuses = await BurstAsync(requests);
        Assert.True(statuses[30] == HttpStatusCode.TooManyRequests, $"30 HEAD misses then 1 GET miss: {Summary(statuses)}");
    }

    [Fact]
    public async Task Thirty_misses_from_one_address_are_all_plain_404s()
    {
        // Guards against an over-eager limiter once BE-031 lands (passes today).
        var ip = TestIps.RandomV4();
        var statuses = await BurstAsync(Enumerable.Range(0, 30).Select(_ => ($"/{RandomMissCode()}", ip, HttpMethod.Get)));
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NotFound, s));
    }

    [Fact]
    public async Task Hits_from_different_addresses_are_not_limited_together()
    {
        // 300 hits from one address must not throttle a different address (passes today; guards BE-031).
        var owner = await P.CreateUserAsync();
        var link = await P.CreateLinkAsync(owner);
        var a = TestIps.RandomV4();
        await BurstAsync(Enumerable.Range(0, 300).Select(_ => ($"/{link.Slug}", a, HttpMethod.Get)));
        var other = await BurstAsync([($"/{link.Slug}", TestIps.RandomV4(), HttpMethod.Get)]);
        Assert.Equal(HttpStatusCode.Redirect, other[0]);
    }
}

/// <summary>
/// BE-030 positive path: with Network:KnownProxies configured, X-Forwarded-For from that proxy IS honoured,
/// so two visitors behind the same proxy get separate partitions. The setting names come from the task brief
/// (Network:KnownProxies / KnownNetworks / ForwardLimit). Today the app ignores these keys and trusts every
/// peer, so this test passes today for the wrong reason; it becomes meaningful once BE-030 lands.
/// </summary>
public sealed class TrustedProxyFactory : ZaaglApiFactory
{
    public const string ProxyAddress = "198.51.100.7";

    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
    [
        new("Network:KnownProxies:0", ProxyAddress),
        new("Network:ForwardLimit", "2"),
    ];
}

[Trait("Area", "QA-011")]
public sealed class TrustedProxyTests(TrustedProxyFactory api) : IClassFixture<TrustedProxyFactory>
{
    [Fact]
    [Trait("Guards", "BE-030")]
    public async Task Visitors_behind_a_known_proxy_are_partitioned_by_their_forwarded_address()
    {
        var owner = await api.Principals.CreateUserAsync();
        var link = await api.Principals.CreateLinkAsync(owner, new LinkInput($"https://example.com/qa/proxy/{TestIps.UniqueSuffix()}",
            Policy: new PolicyInput(Password: "proxy oracle")));
        using var client = api.ApiClient();
        var proxy = IPAddress.Parse(TrustedProxyFactory.ProxyAddress);

        HttpRequestMessage Via(string visitor)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, $"/api/unlock/{link.Slug}") { Content = JsonContent.Create(new { password = "wrong" }) };
            r.Headers.Add(RemoteIpStartupFilter.Header, proxy.ToString());
            r.Headers.Add("X-Forwarded-For", visitor);
            return r;
        }

        var visitorA = TestIps.RandomV4().ToString();
        var visitorB = TestIps.RandomV4().ToString();
        var a = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) a.Add((await client.SendAsync(Via(visitorA))).StatusCode);
        var b = (await client.SendAsync(Via(visitorB))).StatusCode;

        Assert.Equal(HttpStatusCode.TooManyRequests, a[10]); // visitor A's own partition is exhausted
        Assert.NotEqual(HttpStatusCode.TooManyRequests, b);   // visitor B behind the same proxy is not
    }
}
