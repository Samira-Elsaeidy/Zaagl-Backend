using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Endpoints;
using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Links.Tests;

/// <summary>
/// PD-41: social sign-in one-time codes and SignalR hub tickets (short-lived, single-use, hashed, bound to a user),
/// and spec §7.4 refresh_raced → 409. Real local dev Postgres; the Identity migration is applied once.
/// </summary>
public sealed class OneTimeCodeTests : IClassFixture<RealApiFactory>, IDisposable
{
    private const string ConnectionString = RealApiFactory.ConnectionString;
    private static readonly Lazy<bool> Migrated = new(() =>
    {
        using var db = NewDb();
        db.Database.Migrate();
        return true;
    });

    private readonly RealApiFactory _api;
    private readonly IdentityDbContext _db;
    private readonly ManualTime _time = new();

    public OneTimeCodeTests(RealApiFactory api)
    {
        _ = Migrated.Value;
        _api = api;
        _db = NewDb();
    }

    public void Dispose() => _db.Dispose();

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IdentityDbContext NewDb()
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(builder, ConnectionString);
        return new IdentityDbContext(builder.Options);
    }

    private OneTimeCodeService Codes(IdentityDbContext? db = null) => new(db ?? _db, _time);

    // ---- The store ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_code_is_consumed_exactly_once_and_only_its_hash_is_stored()
    {
        var user = Guid.NewGuid();
        var issued = await Codes().IssueAsync(user, OneTimeCodePurpose.ExternalLogin, default);

        Assert.Equal(43, issued.Code.Length);
        Assert.Equal(_time.Now.UtcDateTime.AddSeconds(60), issued.ExpiresAt);
        Assert.False(await _db.OneTimeCodes.AnyAsync(c => c.CodeHash == issued.Code));

        Assert.Equal(user, await Codes().ConsumeAsync(issued.Code, OneTimeCodePurpose.ExternalLogin, default));
        Assert.Null(await Codes().ConsumeAsync(issued.Code, OneTimeCodePurpose.ExternalLogin, default));
    }

    [Fact]
    public async Task A_code_expires_after_60_seconds()
    {
        var issued = await Codes().IssueAsync(Guid.NewGuid(), OneTimeCodePurpose.HubTicket, default);
        _time.Now = _time.Now.AddSeconds(61);
        Assert.Null(await Codes().PeekAsync(issued.Code, OneTimeCodePurpose.HubTicket, default));
        Assert.Null(await Codes().ConsumeAsync(issued.Code, OneTimeCodePurpose.HubTicket, default));
    }

    [Fact]
    public async Task A_code_works_only_for_its_purpose_and_peek_does_not_consume()
    {
        var user = Guid.NewGuid();
        var issued = await Codes().IssueAsync(user, OneTimeCodePurpose.HubTicket, default);

        Assert.Null(await Codes().ConsumeAsync(issued.Code, OneTimeCodePurpose.ExternalLogin, default));
        Assert.Equal(user, await Codes().PeekAsync(issued.Code, OneTimeCodePurpose.HubTicket, default));
        Assert.Equal(user, await Codes().ConsumeAsync(issued.Code, OneTimeCodePurpose.HubTicket, default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789012345678901234567890123456789+/=")]
    public async Task Malformed_codes_are_refused(string? code) =>
        Assert.Null(await Codes().ConsumeAsync(code, OneTimeCodePurpose.ExternalLogin, default));

    [Fact]
    public async Task Concurrent_consumers_of_one_code_have_exactly_one_winner()
    {
        var issued = await Codes().IssueAsync(Guid.NewGuid(), OneTimeCodePurpose.ExternalLogin, default);
        var contexts = Enumerable.Range(0, 8).Select(_ => NewDb()).ToList();
        try
        {
            var results = await Task.WhenAll(contexts.Select(db => Codes(db).ConsumeAsync(issued.Code, OneTimeCodePurpose.ExternalLogin, default)));
            Assert.Equal(1, results.Count(r => r is not null));
        }
        finally
        {
            foreach (var db in contexts) db.Dispose();
        }
    }

    // ---- POST /api/auth/external/exchange -------------------------------------------------------------

    internal async Task<(Guid UserId, string Code)> SocialSignInAsync()
    {
        using var scope = _api.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var user = await identity.ExternalSignInUserAsync("github", Guid.NewGuid().ToString("N"), $"otc-{Guid.NewGuid():N}@example.test", "Otc", default);
        Assert.True(user.IsSuccess, user.Error?.Message);
        var code = await scope.ServiceProvider.GetRequiredService<OneTimeCodeService>().IssueAsync(user.Value, OneTimeCodePurpose.ExternalLogin, default);
        return (user.Value, code.Code);
    }

    internal static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string? code) =>
        await client.PostAsJsonAsync("/api/auth/external/exchange", new { code });

    [Fact]
    public async Task Exchange_returns_the_token_pair_once_then_401_invalid_code()
    {
        var client = _api.ApiClient();
        var (userId, code) = await SocialSignInAsync();

        var first = await ExchangeAsync(client, code);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
        Assert.Equal(userId, body.GetProperty("user").GetProperty("id").GetGuid());

        var second = await ExchangeAsync(client, code);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(IdentityEndpoints.InvalidCode, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(client, null)).StatusCode);
    }

    [Fact]
    public async Task Exchange_refuses_a_hub_ticket()
    {
        var (userId, _) = await SocialSignInAsync();
        var ticket = await Codes().IssueAsync(userId, OneTimeCodePurpose.HubTicket, default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(_api.ApiClient(), ticket.Code)).StatusCode);
    }

    [Fact]
    public async Task Exchange_refuses_an_account_disabled_after_the_callback()
    {
        var (userId, code) = await SocialSignInAsync();
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, DateTime.UtcNow));

        var response = await ExchangeAsync(_api.ApiClient(), code);
        Assert.Equal("account_disabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ---- POST /api/auth/hub-ticket and the hub ------------------------------------------------------

    private async Task<string> AccessTokenAsync()
    {
        var (_, code) = await SocialSignInAsync();
        var body = await (await ExchangeAsync(_api.ApiClient(), code)).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private async Task<string> HubTicketAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/hub-ticket");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow);
        return body.GetProperty("ticket").GetString()!;
    }

    [Fact]
    public async Task Hub_ticket_needs_a_session()
    {
        var response = await _api.ApiClient().PostAsync("/api/auth/hub-ticket", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_ticket_opens_exactly_one_connection()
    {
        var client = _api.ApiClient();
        var ticket = await HubTicketAsync(client);
        var query = $"ticket={Uri.EscapeDataString(ticket)}";

        // Negotiate only checks the ticket.
        var negotiate = await client.PostAsync($"/hubs/analytics/negotiate?negotiateVersion=1&{query}", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var connectionToken = (await negotiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionToken").GetString();

        // The connect request consumes it (long polling's first poll stands in for the WebSocket upgrade here).
        var connect = await client.GetAsync($"/hubs/analytics?id={connectionToken}&{query}");
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        var again = await client.PostAsync($"/hubs/analytics/negotiate?negotiateVersion=1&{query}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task Hub_refuses_an_unknown_ticket()
    {
        var response = await _api.ApiClient().PostAsync($"/hubs/analytics/negotiate?negotiateVersion=1&ticket={new string('a', 43)}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_ignores_an_access_token_in_the_query_string()
    {
        // PD-41: the deprecated ?access_token= sign-in on /hubs is gone; a valid session JWT there is not read.
        var token = await AccessTokenAsync();
        var response = await _api.ApiClient().PostAsync(
            $"/hubs/analytics/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- refresh_raced --------------------------------------------------------------------------------

    [Fact]
    public void Refresh_raced_maps_to_409()
    {
        var result = ApiResults.Problem(new Error("refresh_raced", "raced"));
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}

/// <summary>Tests that change who holds the Admin role in the shared database run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class AdminRoleCollection
{
    public const string Name = "Admin role (shared database)";
}

/// <summary>PD-41: social sign-in must not bypass the Admin TOTP requirement (spec §7.4).</summary>
[Collection(AdminRoleCollection.Name)]
public sealed class SocialSignInAdminTests(RealApiFactory api) : IClassFixture<RealApiFactory>
{
    [Fact]
    public async Task Social_sign_in_refuses_an_admin_account()
    {
        using var helper = new OneTimeCodeTests(api);
        var (userId, code) = await helper.SocialSignInAsync();
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(builder, RealApiFactory.ConnectionString);
        await using var db = new IdentityDbContext(builder.Options);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, "Admin"));
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await OneTimeCodeTests.ExchangeAsync(api.ApiClient(), code)).StatusCode);
        }
        finally
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, (string?)null));
        }
    }
}
