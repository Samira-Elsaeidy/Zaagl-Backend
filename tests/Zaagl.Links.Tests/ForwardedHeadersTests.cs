using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Infrastructure;
using Zaagl.SharedKernel.Net;

namespace Zaagl.Links.Tests;

/// <summary>
/// BE-030 (spec §7.13): X-Forwarded-For is honoured only from exact known proxies (and staging gate ranges),
/// read right to left up to Network:ForwardLimit hops, so an internet client can never choose its own partition.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private const string RemoteHeader = "X-Test-Remote";

    private static async Task<string> ClientIpAsync(NetworkOptions network, string remote, string? forwardedFor, string? forwardedHost = null)
    {
        await using var app = await ApiTestHost.StartAsync(
            services => services.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Apply(o, network)),
            a => a.MapGet("/ip", (HttpContext c) => $"{c.Connection.RemoteIpAddress}|{c.Request.Host}"),
            a =>
            {
                // The test server has no socket: take the connection address from a test-only header first.
                a.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[RemoteHeader].ToString());
                    return next(context);
                });
                a.UseForwardedHeaders();
            });

        var request = new HttpRequestMessage(HttpMethod.Get, "/ip");
        request.Headers.Add(RemoteHeader, remote);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedHost is not null) request.Headers.Add("X-Forwarded-Host", forwardedHost);
        var response = await app.GetTestClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string Ip(string result) => result.Split('|')[0];

    [Fact]
    public async Task With_nothing_configured_no_forwarded_header_is_trusted_not_even_from_loopback()
    {
        var none = new NetworkOptions();
        Assert.Equal("203.0.113.9", Ip(await ClientIpAsync(none, "203.0.113.9", "1.2.3.4")));
        Assert.Equal("127.0.0.1", Ip(await ClientIpAsync(none, "127.0.0.1", "1.2.3.4")));
    }

    [Fact]
    public async Task A_known_proxy_sets_the_client_address_and_host()
    {
        var network = new NetworkOptions { KnownProxies = ["10.0.0.2"] };
        var result = await ClientIpAsync(network, "10.0.0.2", "198.51.100.7", "zaa.gl");
        Assert.Equal("198.51.100.7|zaa.gl", result);
    }

    [Fact]
    public async Task A_forged_header_from_a_direct_client_does_not_change_its_partition()
    {
        var network = new NetworkOptions { KnownProxies = ["10.0.0.2", "10.0.0.3"] };
        Assert.Equal("203.0.113.9", Ip(await ClientIpAsync(network, "203.0.113.9", "1.2.3.4, 10.0.0.2")));
    }

    [Fact]
    public async Task A_spoofed_leftmost_entry_behind_the_proxy_is_ignored()
    {
        // Client 198.51.100.7 sent "X-Forwarded-For: 6.6.6.6"; the proxy appended the real address.
        var network = new NetworkOptions { KnownProxies = ["10.0.0.2"] };
        Assert.Equal("198.51.100.7", Ip(await ClientIpAsync(network, "10.0.0.2", "6.6.6.6, 198.51.100.7")));
    }

    [Fact]
    public async Task Proxy_then_bff_resolves_the_visitor_and_two_visitors_get_separate_partitions()
    {
        // Browser -> proxy (10.0.0.2) -> BFF (10.0.0.3, appends the proxy address) -> api.
        var network = new NetworkOptions { KnownProxies = ["10.0.0.2", "10.0.0.3"], ForwardLimit = 2 };
        var first = Ip(await ClientIpAsync(network, "10.0.0.3", "6.6.6.6, 198.51.100.7, 10.0.0.2"));
        var second = Ip(await ClientIpAsync(network, "10.0.0.3", "203.0.113.50, 10.0.0.2"));

        Assert.Equal("198.51.100.7", first);
        Assert.Equal("203.0.113.50", second);
        Assert.NotEqual(IpPartition.Key(IPAddress.Parse(first)), IpPartition.Key(IPAddress.Parse(second)));
    }

    [Fact]
    public async Task Staging_gate_ranges_are_trusted_through_known_networks()
    {
        var network = new NetworkOptions { KnownProxies = ["10.0.0.2"], KnownNetworks = ["192.0.2.0/24"], ForwardLimit = 3 };
        Assert.Equal("198.51.100.7", Ip(await ClientIpAsync(network, "10.0.0.2", "198.51.100.7, 192.0.2.44")));
    }

    [Theory]
    [InlineData("10.0.0.0/8", null, 2)]
    [InlineData("not-an-ip", null, 2)]
    [InlineData(null, "10.0.0.1", 2)]
    [InlineData(null, null, 0)]
    public void Invalid_settings_fail_startup(string? proxy, string? range, int forwardLimit)
    {
        var network = new NetworkOptions
        {
            KnownProxies = proxy is null ? [] : [proxy],
            KnownNetworks = range is null ? [] : [range],
            ForwardLimit = forwardLimit,
        };
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.Apply(new ForwardedHeadersOptions(), network));
    }

    [Fact]
    public void A_configured_proxy_forwards_for_proto_and_host_with_a_default_limit_of_two()
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersSetup.Apply(options, new NetworkOptions { KnownProxies = ["10.0.0.2"] });
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, options.ForwardedHeaders);
        Assert.Equal(2, options.ForwardLimit);
        Assert.Equal([IPAddress.Parse("10.0.0.2")], options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }

    [Fact]
    public void Nothing_configured_switches_forwarding_off()
    {
        // The framework treats two empty lists as "trust everyone"; Apply must never leave it in that state.
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersSetup.Apply(options, new NetworkOptions());
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }
}
