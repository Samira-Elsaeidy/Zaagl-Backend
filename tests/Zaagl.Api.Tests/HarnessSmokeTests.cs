using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>QA-006 acceptance: create a link via HTTP on the API host, follow it on the short host.</summary>
[Collection(ApiCollection.Name)]
[Trait("Area", "QA-006")]
public sealed class HarnessSmokeTests(ZaaglApiFactory api, ITestOutputHelper output)
{
    [Fact]
    public async Task Link_created_over_http_redirects_on_the_short_host()
    {
        var owner = await api.Principals.CreateUserAsync();
        var destination = $"https://example.com/qa/smoke/{TestIps.UniqueSuffix()}";
        var link = await api.Principals.CreateLinkViaHttpAsync(owner, new LinkInput(destination));

        using var shortClient = api.ShortClient();
        var response = await shortClient.GetAsync($"/{link.Slug}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(destination, response.Headers.Location?.ToString());
    }

    [Fact]
    public void Remote_ip_seam_and_route_table_are_visible()
    {
        var routes = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}")
            .Order().ToList();
        foreach (var r in routes) output.WriteLine(r);
        Assert.NotEmpty(routes);
    }
}
