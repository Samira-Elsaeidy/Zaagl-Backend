using Microsoft.AspNetCore.Http;
using Zaagl.Api.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>
/// Proves the host-routing MECHANISM (spec §1.2) is correct with two genuinely different configured
/// hosts - independent of this dev environment pointing both Hosts:Short and Hosts:Api at the same value.
/// </summary>
public class HostRoutingTests
{
    private static readonly HostRoutingOptions TwoHosts = new() { Short = "zaa.gl", Api = "api.zaa.gl" };

    [Theory]
    [InlineData("/abcde")]
    [InlineData("/Xy12z")]
    public void Redirect_shaped_paths_are_allowed_only_on_the_short_host(string path)
    {
        Assert.True(HostRoutingDecision.IsAllowed("zaa.gl", path, TwoHosts));
        Assert.False(HostRoutingDecision.IsAllowed("api.zaa.gl", path, TwoHosts));
        Assert.False(HostRoutingDecision.IsAllowed("some-other-host.example", path, TwoHosts));
    }

    [Theory]
    [InlineData("/api/links")]
    [InlineData("/api/auth/login")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/account/confirm-email")]
    public void Api_shaped_paths_are_allowed_only_on_the_api_host(string path)
    {
        Assert.True(HostRoutingDecision.IsAllowed("api.zaa.gl", path, TwoHosts));
        Assert.False(HostRoutingDecision.IsAllowed("zaa.gl", path, TwoHosts));
    }

    [Fact]
    public void A_host_with_a_port_matches_the_same_configured_host()
    {
        Assert.True(HostRoutingDecision.IsAllowed("zaa.gl:443", "/abcde", TwoHosts));
        Assert.True(HostRoutingDecision.IsAllowed("zaa.gl", "/abcde", new HostRoutingOptions { Short = "zaa.gl:443", Api = "api.zaa.gl" }));
    }

    [Fact]
    public void Both_hosts_pointing_at_the_same_value_allows_everything_on_it()
    {
        // This is this dev environment's actual configuration: no real second domain exists locally.
        var sameHost = new HostRoutingOptions { Short = "localhost", Api = "localhost" };
        Assert.True(HostRoutingDecision.IsAllowed("localhost", "/abcde", sameHost));
        Assert.True(HostRoutingDecision.IsAllowed("localhost", "/api/links", sameHost));
    }

    [Theory]
    [InlineData("/api/links", false)]
    [InlineData("/openapi/v1.json", false)]
    [InlineData("/account/confirm-email", false)]
    [InlineData("/hubs/analytics", false)]
    [InlineData("/scalar", false)]
    [InlineData("/abcde", true)]
    [InlineData("/", true)]
    public void IsRedirectShapedPath_classifies_correctly(string path, bool expectedRedirectShaped)
    {
        Assert.Equal(expectedRedirectShaped, HostRoutingDecision.IsRedirectShapedPath(path));
    }
}
