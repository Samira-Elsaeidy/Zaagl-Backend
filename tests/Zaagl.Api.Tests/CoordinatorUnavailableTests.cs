using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Infrastructure;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel.Coordination;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Api.Tests;

/// <summary>The status endpoint stays healthy (the creation gate is Ok) but every lease answers 503: the "coordinator is not leasing" case.</summary>
public sealed class NotLeasingFactory : SnippetsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var fingerprint = KeyFingerprint.Compute(ScrambleKey);
            services.AddHttpClient(nameof(CoordinatorLeaseClient)).ConfigurePrimaryHttpMessageHandler(() => new NotLeasingHandler(fingerprint));
        });
    }

    private sealed class NotLeasingHandler(string fingerprint) : HttpMessageHandler
    {
        private readonly FakeCoordinatorHandler _inner = new(fingerprint);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath == "/v1/leases"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : ((HttpMessageInvoker)new HttpMessageInvoker(_inner)).SendAsync(request, ct);
    }
}

/// <summary>Operator-facing failures never reach a user as raw .NET text: a coordinator that cannot lease is a clean 503 with Retry-After,
/// and anything else that escapes an endpoint is a generic problem+json 500 (no type name, message or stack trace).</summary>
public sealed class CoordinatorUnavailableTests(NotLeasingFactory api) : IClassFixture<NotLeasingFactory>
{
    private static async Task AssertCleanUnavailableAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single()) > 0);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("service_unavailable", body.GetProperty("code").GetString());
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.StartsWith("https://zaa.gl/problems/", body.GetProperty("type").GetString());
        AssertNoLeak(text);
    }

    private static void AssertNoLeak(string text)
    {
        foreach (var bad in new[] { "System.", "Exception", "InvalidOperation", "coordinator", "   at ", "Zaagl.", "Npgsql" })
            Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_a_link_while_the_coordinator_cannot_lease_is_a_clean_503()
    {
        using var client = api.ApiClient(bearer: (await api.Principals.CreateUserAsync(starter: false, "nolease1")).AccessToken);
        using var response = await client.PostAsJsonAsync("/api/links", new LinkInput($"https://example.com/qa/nolease/{TestIps.UniqueSuffix()}"));
        await AssertCleanUnavailableAsync(response);
    }

    [Fact]
    public async Task Creating_a_snippet_while_the_coordinator_cannot_lease_is_a_clean_503_and_releases_what_it_reserved()
    {
        var user = await api.Principals.CreateUserAsync(starter: false, "nolease2");
        using var client = api.ApiClient(bearer: user.AccessToken);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/snippets") { Content = JsonContent.Create(new { content = "hello", @public = true }, options: TestPrincipals.Json) };
        request.Headers.Add("Idempotency-Key", "nl-" + TestIps.UniqueSuffix());
        using var response = await client.SendAsync(request);
        await AssertCleanUnavailableAsync(response);

        using var list = await client.GetAsync("/api/resources?kind=snippet&limit=50");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_exception_handler_maps_the_coordinator_to_503_and_everything_else_to_a_generic_500()
    {
        var handler = new ApiExceptionHandler();

        var unavailable = new DefaultHttpContext { RequestServices = api.Services };
        unavailable.Response.Body = new MemoryStream();
        Assert.True(await handler.TryHandleAsync(unavailable, new CoordinatorUnavailableException("The coordinator is not leasing right now (blocked or not initialised)."), default));
        Assert.Equal(503, unavailable.Response.StatusCode);
        Assert.Equal("5", unavailable.Response.Headers.RetryAfter.ToString());

        var other = new DefaultHttpContext { RequestServices = api.Services };
        other.Response.Body = new MemoryStream();
        Assert.True(await handler.TryHandleAsync(other, new InvalidOperationException("secret detail Npgsql.PostgresException at Zaagl.Modules.X"), default));
        Assert.Equal(500, other.Response.StatusCode);
        other.Response.Body.Position = 0;
        var text = await new StreamReader(other.Response.Body).ReadToEndAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("internal-error", body.GetProperty("code").GetString());
        Assert.DoesNotContain("secret", text);
        AssertNoLeak(text);
    }

    [Fact]
    public async Task The_lease_client_turns_every_unavailable_answer_into_the_typed_exception()
    {
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Conflict, HttpStatusCode.InsufficientStorage, HttpStatusCode.InternalServerError })
        {
            var client = new CoordinatorLeaseClient(new HttpClient(new Fixed(status)) { BaseAddress = new Uri("http://c.test") }, "fp");
            await Assert.ThrowsAsync<CoordinatorUnavailableException>(() => client.NextAsync(default));
        }
        var down = new CoordinatorLeaseClient(new HttpClient(new Fixed(null)) { BaseAddress = new Uri("http://c.test") }, "fp");
        await Assert.ThrowsAsync<CoordinatorUnavailableException>(() => down.NextAsync(default));
    }

    private sealed class Fixed(HttpStatusCode? status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            status is null ? throw new HttpRequestException("connection refused") : Task.FromResult(new HttpResponseMessage(status.Value));
    }
}
