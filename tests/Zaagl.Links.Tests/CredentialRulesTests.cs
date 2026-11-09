using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>
/// On the real app (real JWT bearer and API-key handlers, shared PostgreSQL):
///  - PD-36, spec §7.2 step 1 and MUST NOT #27: wrong, revoked or malformed API keys and expired, forged or
///    malformed JWTs on POST /api/links get 401, never a guest link; no credentials stays a guest request.
///  - R1, spec §7.4: /api/auth/me, /api/billing/me and /api/auth/logout-everywhere refuse API keys.
///  - AZ-01, spec §7.4: the Admin enrol-only token works only on /totp/enroll and /totp/confirm.
/// </summary>
public sealed class CredentialRulesTests(RealApiFactory api) : IClassFixture<RealApiFactory>, IAsyncLifetime
{
    private User _user = null!;
    private User _admin = null!;
    private string _activeKey = null!;
    private string _readOnlyKey = null!;
    private string _revokedKey = null!;

    public async Task InitializeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        _user = new User { Email = $"pd36-{Guid.NewGuid():N}@example.test", DisplayName = "PD-36", EmailConfirmed = true };
        // No Role row: the enrol-only token carries only sub/sstamp, and a persisted Admin would race
        // AccountDeletionTests' "last remaining admin" check on the shared database.
        _admin = new User { Email = $"az01-{Guid.NewGuid():N}@example.test", DisplayName = "AZ-01", EmailConfirmed = true };
        db.Users.AddRange(_user, _admin);
        _activeKey = AddKey(db, _user.Id, revoked: false, "links:read", "links:write", "stats:read");
        _readOnlyKey = AddKey(db, _user.Id, revoked: false, "links:read");
        _revokedKey = AddKey(db, _user.Id, revoked: true, "links:read", "links:write", "stats:read");
        await db.SaveChangesAsync();

        // BE-003 / spec §7.4: a key authenticates only while its owner's plan has the ApiKeys capability (Starter).
        var billing = scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Billing.Infrastructure.BillingDbContext>();
        billing.Subscriptions.Add(new Zaagl.Modules.Billing.Domain.Subscription
        {
            UserId = _user.Id,
            Tier = Zaagl.Modules.Billing.Contracts.PlanTier.Starter,
            Status = "active",
            UpdatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
        });
        await billing.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.ApiKeys.Where(k => k.UserId == _user.Id).ExecuteDeleteAsync();
        await scope.ServiceProvider.GetRequiredService<Zaagl.Modules.Billing.Infrastructure.BillingDbContext>()
            .Subscriptions.Where(s => s.UserId == _user.Id).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == _user.Id || u.Id == _admin.Id).ExecuteDeleteAsync();
    }

    private static string AddKey(IdentityDbContext db, Guid userId, bool revoked, params string[] scopes)
    {
        var secret = "zgl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.ApiKeys.Add(new ApiKey
        {
            UserId = userId,
            Name = "credential-rules-test",
            Prefix = secret[..8],
            KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            Scopes = scopes,
            RevokedAt = revoked ? TimeProvider.System.GetUtcNow().UtcDateTime : null,
        });
        return secret;
    }

    private string SessionToken(User user) => api.Services.GetRequiredService<TokenService>().IssueAccessToken(user).Token;

    private string EnrolOnlyToken() => api.Services.GetRequiredService<TokenService>().IssueEnrolOnlyToken(_admin).Token;

    private string Jwt(User user, DateTime expires, string? key = null)
    {
        var jwt = api.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var entry = jwt.SigningKeyEntry();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            NotBefore = expires.AddMinutes(-30),
            IssuedAt = expires.AddMinutes(-30),
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? entry.Key)) { KeyId = entry.Kid }, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([new Claim("sub", user.Id.ToString()), new Claim("sstamp", user.SecurityStamp.ToString())]),
        });
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer = null, string? apiKey = null, object? body = null)
    {
        using var client = api.ApiClient();
        var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (apiKey is not null) request.Headers.Add("X-Api-Key", apiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static object NewLink() => new { destinationUrl = $"https://example.com/pd36/{Guid.NewGuid():N}" };

    // ---- PD-36 --------------------------------------------------------------------------------------------

    public static TheoryData<string> BadApiKeys => new() { "wrong", "revoked", "malformed" };

    [Theory]
    [MemberData(nameof(BadApiKeys))]
    public async Task A_bad_api_key_on_create_link_gets_401_not_a_guest_link(string kind)
    {
        var key = kind switch
        {
            "wrong" => "zgl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            "revoked" => _revokedKey,
            _ => "not-an-api-key",
        };
        var response = await SendAsync(HttpMethod.Post, "/api/links", apiKey: key, body: NewLink());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> BadJwts => new() { "expired", "forged", "malformed", "not-bearer" };

    [Theory]
    [MemberData(nameof(BadJwts))]
    public async Task A_bad_jwt_on_create_link_gets_401_not_a_guest_link(string kind)
    {
        using var client = api.ApiClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/links") { Content = JsonContent.Create(NewLink()) };
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        request.Headers.Authorization = kind switch
        {
            "expired" => new AuthenticationHeaderValue("Bearer", Jwt(_user, now.AddMinutes(-10))),
            "forged" => new AuthenticationHeaderValue("Bearer", Jwt(_user, now.AddMinutes(10), key: "some-other-signing-key-0123456789abcdef-0123")),
            "malformed" => new AuthenticationHeaderValue("Bearer", "not.a.jwt"),
            _ => new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz"),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task No_credentials_stays_a_guest_request()
    {
        // The guest path answers (CAPTCHA, guest limits, ...) - never 401.
        var response = await SendAsync(HttpMethod.Post, "/api/links", body: NewLink());
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_key_is_still_authenticated_on_create_link()
    {
        // links:read only: authenticated, so the scope check answers 403 (a guest would never get 403).
        var response = await SendAsync(HttpMethod.Post, "/api/links", apiKey: _readOnlyKey, body: NewLink());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- R1: JWT-session-only account endpoints -----------------------------------------------------------

    public static TheoryData<string, string> AccountRoutes => new()
    {
        { "GET", "/api/auth/me" },
        { "GET", "/api/billing/me" },
        { "POST", "/api/auth/logout-everywhere" },
    };

    [Theory]
    [MemberData(nameof(AccountRoutes))]
    public async Task An_api_key_with_every_scope_cannot_use_account_endpoints(string method, string path)
    {
        var response = await SendAsync(new HttpMethod(method), path, apiKey: _activeKey);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_session_jwt_can_read_auth_me()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/auth/me", bearer: SessionToken(_user));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- AZ-01: enrol-only token ----------------------------------------------------------------------------

    public static TheoryData<string, string> NonEnrolRoutes => new()
    {
        { "GET", "/api/auth/me" },
        { "POST", "/api/api-keys" },
        { "GET", "/api/api-keys" },
        { "GET", "/api/links" },
        { "POST", "/api/links" },
        { "GET", "/api/folders" },
        { "GET", "/api/billing/me" },
        { "GET", "/api/me/export" },
        { "POST", "/api/auth/logout-everywhere" },
        { "GET", "/api/admin/users" },
    };

    [Theory]
    [MemberData(nameof(NonEnrolRoutes))]
    public async Task The_enrol_only_token_is_refused_outside_totp_enrolment(string method, string path)
    {
        object? body = method == "POST" ? (path == "/api/links" ? NewLink() : new { name = "az01", scopes = new[] { "links:read" } }) : null;
        var response = await SendAsync(new HttpMethod(method), path, bearer: EnrolOnlyToken(), body: body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_enrol_only_token_works_on_totp_enroll_and_confirm()
    {
        var token = EnrolOnlyToken();
        var enroll = await SendAsync(HttpMethod.Post, "/api/auth/totp/enroll", bearer: token);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);

        // A wrong code is a validation error from the handler, not an authentication failure.
        var confirm = await SendAsync(HttpMethod.Post, "/api/auth/totp/confirm", bearer: token, body: new { code = "000000" });
        Assert.NotEqual(HttpStatusCode.Unauthorized, confirm.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, confirm.StatusCode);
    }
}
