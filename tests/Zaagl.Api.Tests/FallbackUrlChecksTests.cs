using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Tests;

/// <summary>
/// PD-44 (addendum §17 note 3): a fallback URL gets the same checks as a destination (blocklist and Google Web
/// Risk) when it is set or changed: on create (POST /api/links) and on update (PUT /api/links/{id}) a blocked
/// or unsafe fallback is refused with 400.
///
/// Error code: the coordinator's brief says "destination-blocked / destination-unsafe"; the backend currently
/// answers "fallback-blocked / fallback-unsafe" (LinkService.cs). The decision text names no code, so these
/// tests accept either prefix and only require 400 plus "blocked" / "unsafe" in the problem body. Open contract
/// question recorded in the baseline.
///
/// Uses its own host (<see cref="FallbackChecksApiFactory"/>) so nothing global is written to the shared DB:
/// the blocklist entry comes from the Safety:DenyRedirectorHosts setting, and Web Risk is a fake that flags
/// any URL containing <see cref="FakeReputationChecker.UnsafeMarker"/>.
/// </summary>
[Trait("Decision", "PD-44")]
[Trait("Area", "QA-009")]
public sealed class FallbackUrlChecksTests(FallbackChecksApiFactory api) : IClassFixture<FallbackChecksApiFactory>
{
    private static string Safe() => $"https://example.com/qa/pd44/{TestIps.UniqueSuffix()}";
    private static string Blocked() => $"https://{FallbackChecksApiFactory.BlockedHost}/qa/{TestIps.UniqueSuffix()}";
    private static string Unsafe() => $"https://example.net/{FakeReputationChecker.UnsafeMarker}/{TestIps.UniqueSuffix()}";

    private static async Task AssertRefused(HttpResponseMessage r, string word, string what)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{what} -> {(int)r.StatusCode} {body}");
        Assert.True(body.Contains(word, StringComparison.OrdinalIgnoreCase), $"{what}: problem body lacks '{word}': {body}");
    }

    [Fact]
    public async Task Oracle_the_same_urls_are_refused_as_destinations()
    {
        // Proves the fake blocklist entry and the fake Web Risk are live in this host.
        var owner = await api.Principals.CreateUserAsync(tag: "pd44-oracle");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        await AssertRefused(await client.PostAsJsonAsync("/api/links", new LinkInput(Blocked()), TestPrincipals.Json), "blocked", "blocked destination");
        await AssertRefused(await client.PostAsJsonAsync("/api/links", new LinkInput(Unsafe()), TestPrincipals.Json), "unsafe", "unsafe destination");
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("unsafe")]
    public async Task Create_with_a_bad_fallback_is_refused(string kind)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "pd44-create");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var fallback = kind == "blocked" ? Blocked() : Unsafe();
        var r = await client.PostAsJsonAsync("/api/links", new LinkInput(Safe(), Policy: new PolicyInput(FallbackUrl: fallback)), TestPrincipals.Json);
        await AssertRefused(r, kind, $"create with {kind} fallback");
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("unsafe")]
    public async Task Update_to_a_bad_fallback_is_refused_and_the_link_keeps_its_old_fallback(string kind)
    {
        var owner = await api.Principals.CreateUserAsync(tag: "pd44-update");
        var destination = Safe();
        var goodFallback = $"https://example.org/qa/pd44/{TestIps.UniqueSuffix()}";
        var link = await api.Principals.CreateLinkAsync(owner, new LinkInput(destination, Policy: new PolicyInput(FallbackUrl: goodFallback)));
        using var client = api.ApiClient(bearer: owner.AccessToken);

        var bad = kind == "blocked" ? Blocked() : Unsafe();
        var r = await client.PutAsJsonAsync($"/api/links/{link.Id}", new LinkInput(destination, Policy: new PolicyInput(FallbackUrl: bad)), TestPrincipals.Json);
        await AssertRefused(r, kind, $"update to {kind} fallback");

        var after = await client.GetFromJsonAsync<LinkDto>($"/api/links/{link.Id}", TestPrincipals.Json);
        Assert.Equal(goodFallback, after!.Policy.FallbackUrl);
    }

    [Fact]
    public async Task A_safe_fallback_is_accepted()
    {
        var owner = await api.Principals.CreateUserAsync(tag: "pd44-ok");
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var r = await client.PostAsJsonAsync("/api/links", new LinkInput(Safe(), Policy: new PolicyInput(FallbackUrl: Safe())), TestPrincipals.Json);
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"safe fallback -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
    }
}

/// <summary>Own host: one extra blocklisted host via configuration, and a fake Web Risk checker.</summary>
public sealed class FallbackChecksApiFactory : ZaaglApiFactory
{
    public const string BlockedHost = "blocked-pd44.qa.test";

    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings() =>
        [new("Safety:DenyRedirectorHosts:99", BlockedHost)];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUrlReputationChecker>();
            services.AddSingleton<IUrlReputationChecker, FakeReputationChecker>();
        });
    }
}

public sealed class FakeReputationChecker : IUrlReputationChecker
{
    public const string UnsafeMarker = "qa-webrisk-malware";

    public Task<ReputationResult> CheckAsync(string url, CancellationToken cancellationToken) =>
        Task.FromResult(url.Contains(UnsafeMarker, StringComparison.Ordinal)
            ? new ReputationResult(false, "MALWARE")
            : new ReputationResult(true));
}
