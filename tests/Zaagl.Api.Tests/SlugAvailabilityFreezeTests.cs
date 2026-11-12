using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Api.Tests;

/// <summary>QA: the live availability checker said "Available" while creating the alias failed with alias-frozen.
/// <c>GET /api/links/slug-available</c> now reports an active freeze (code + frozenUntil) for otherwise-free slugs.
/// Run against a scratch database (ZAAGL_TEST_PG): the freeze row is global while it exists.</summary>
public sealed class SlugAvailabilityFreezeTests(ZaaglApiFactory api) : IClassFixture<ZaaglApiFactory>
{
    private const string Reason = "qa-availability-freeze";

    [Fact]
    public async Task Availability_reports_an_active_alias_freeze_and_stays_accurate_for_taken_slugs()
    {
        var owner = await api.Principals.CreateUserAsync(starter: false);
        using var client = api.ApiClient(bearer: owner.AccessToken);
        var until = DateTime.UtcNow.AddDays(20);
        Guid freezeId;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
            var freeze = new AliasFreeze { StartsAt = DateTime.UtcNow.AddMinutes(-5), Until = until, Source = "operator", Reason = Reason };
            db.AliasFreezes.Add(freeze);
            await db.SaveChangesAsync();
            freezeId = freeze.Id;
        }

        try
        {
            var free = await client.GetAsync("/api/links/slug-available?slug=qa-free-" + TestIps.UniqueSuffix());
            Assert.Equal(HttpStatusCode.OK, free.StatusCode);
            var body = JsonDocument.Parse(await free.Content.ReadAsStringAsync()).RootElement;
            Assert.False(body.GetProperty("available").GetBoolean());
            Assert.Equal("alias-frozen", body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("frozenUntil").GetDateTime() > DateTime.UtcNow.AddDays(19));
        }
        finally
        {
            await using var scope = api.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LinksDbContext>().AliasFreezes.Where(f => f.Id == freezeId).ExecuteDeleteAsync();
        }
    }

    /// <summary>QA: a safety-disabled link (HTTP 410 for visitors) showed "Active" in the owner's library because the
    /// owner DTO carried no disabled information. It now does; only the system-written safety reason is returned.</summary>
    [Theory]
    [InlineData("safety", "Flagged by re-check: MALWARE", "Flagged by re-check: MALWARE")]
    [InlineData("abuse", "internal moderator note", null)]
    [InlineData("admin", "internal moderator note", null)]
    public async Task Owner_dto_reports_a_disabled_link_and_hides_internal_reasons(string kind, string reason, string? exposed)
    {
        var owner = await api.Principals.CreateUserAsync(starter: false);
        var link = await api.Principals.CreateLinkAsync(owner);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LinksDbContext>().Links.Where(l => l.Id == link.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.DisabledAt, DateTime.UtcNow)
                    .SetProperty(l => l.DisabledKind, kind).SetProperty(l => l.DisabledReason, reason));
        }

        using var client = api.ApiClient(bearer: owner.AccessToken);
        var response = await client.GetAsync($"/api/links/{link.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(kind, body.GetProperty("disabledKind").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("disabledAt").ValueKind);
        Assert.Equal(exposed, body.GetProperty("disabledReason").GetString());
    }
}
