using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Zaagl.Api.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>BE-005 (PD-03, ARC-008 §1, §4.1, §10): the user-content host, its header policy and cookie rules.</summary>
public sealed class UserContentHostTests
{
    private static readonly HostRoutingOptions Production = new() { Short = "zaa.gl", Api = "api.zaa.gl", UserContent = "usercontent.zaa.gl" };

    [Theory]
    [InlineData("usercontent.zaa.gl", "/internal/delivery/abc.def", true)]
    [InlineData("usercontent.zaa.gl", "/api/links", false)]
    [InlineData("usercontent.zaa.gl", "/abcde", false)]
    [InlineData("api.zaa.gl", "/internal/delivery/abc.def", false)]
    [InlineData("zaa.gl", "/internal/delivery/abc.def", false)]
    [InlineData("zaa.gl", "/internal", true)] // one segment: still a possible alias on the short host
    [InlineData("zaa.gl", "/abcde", true)]
    [InlineData("api.zaa.gl", "/api/links", true)]
    public void Each_host_serves_only_its_paths(string host, string path, bool allowed) =>
        Assert.Equal(allowed, HostRoutingDecision.IsAllowed(host, new PathString(path), Production));

    [Fact]
    public void An_unset_or_colliding_user_content_host_changes_nothing()
    {
        var dev = new HostRoutingOptions(); // Short = Api = localhost, UserContent empty
        Assert.True(HostRoutingDecision.IsAllowed("localhost", "/api/links", dev));
        Assert.True(HostRoutingDecision.IsAllowed("localhost", "/abcde", dev));

        var colliding = new HostRoutingOptions { UserContent = "localhost" };
        Assert.False(HostRoutingDecision.IsUserContentHost("localhost", colliding));
        Assert.True(HostRoutingDecision.IsAllowed("localhost", "/api/links", colliding));
    }

    [Fact]
    public void Short_and_user_content_responses_never_carry_cookies_and_user_content_is_inert()
    {
        var shortHeaders = new HeaderDictionary { [HeaderNames.SetCookie] = "a=b" };
        HostHeaderPolicy.Apply(HostRole.Short, shortHeaders);
        Assert.False(shortHeaders.ContainsKey(HeaderNames.SetCookie));

        var ucd = new HeaderDictionary { [HeaderNames.SetCookie] = "a=b" };
        HostHeaderPolicy.Apply(HostRole.UserContent, ucd);
        Assert.False(ucd.ContainsKey(HeaderNames.SetCookie));
        Assert.Equal("nosniff", ucd[HeaderNames.XContentTypeOptions]);
        Assert.StartsWith("sandbox;", ucd[HeaderNames.ContentSecurityPolicy].ToString());
        Assert.Equal("private, no-store", ucd[HeaderNames.CacheControl]);
        Assert.Equal("no-referrer", ucd["Referrer-Policy"]);

        var api = new HeaderDictionary { [HeaderNames.SetCookie] = "a=b" }; // PD-46: OAuth handshake cookies on api only
        HostHeaderPolicy.Apply(HostRole.Api, api);
        Assert.True(api.ContainsKey(HeaderNames.SetCookie));
    }

    [Fact]
    public void Roles_follow_host_and_path()
    {
        Assert.Equal(HostRole.UserContent, HostRoutingDecision.RoleOf("usercontent.zaa.gl", "/internal/delivery/x", Production));
        Assert.Equal(HostRole.Short, HostRoutingDecision.RoleOf("zaa.gl", "/abcde", Production));
        Assert.Equal(HostRole.Api, HostRoutingDecision.RoleOf("api.zaa.gl", "/api/links", Production));
        Assert.Equal(HostRole.Short, HostRoutingDecision.RoleOf("localhost", "/abcde", new HostRoutingOptions()));
    }
}
