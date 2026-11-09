using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Modules.Billing.Contracts;
using Zaagl.Modules.Billing.Domain;
using Zaagl.Modules.Billing.Infrastructure;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Tests.Harness;

public sealed record TestUser(Guid Id, string Email, string Password, string AccessToken);

/// <summary>
/// Creates principals through the real product services (IdentityService register, confirm, login,
/// TOTP enrol/confirm, API-key create) and links through LinkService or HTTP.
///
/// Two direct writes, both on rows this harness just created: the plan (billing.subscriptions, because the
/// only product path to Starter is a signed Stripe webhook) and the Admin role (identity.users.role, because
/// the only product path is AdminBootstrap at startup, which is a global side effect on the shared DB).
/// </summary>
public sealed class TestPrincipals(ZaaglApiFactory factory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TestUser> CreateUserAsync(bool starter = true, string tag = "user")
    {
        var (id, email, password) = await RegisterConfirmedAsync(tag, starter);
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var login = await identity.LoginAsync(new LoginRequest(email, password, null), PartitionTag(), CancellationToken.None);
        if (login.Session is null) throw new InvalidOperationException($"Login failed: {login.Error}");
        return new TestUser(id, email, password, login.Session.AccessToken);
    }

    /// <summary>An Admin with TOTP enrolled and confirmed, signed in with a valid code (a full admin session).</summary>
    public async Task<TestUser> CreateAdminAsync()
    {
        var (id, email, password) = await RegisterConfirmedAsync("admin", starter: true);
        using var scope = factory.Services.CreateScope();
        await SetRoleAsync(scope, id, "Admin");
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var enrol = await identity.StartTotpEnrollmentAsync(id, CancellationToken.None);
        var secret = enrol.Value?.AuthenticatorKey ?? throw new InvalidOperationException($"TOTP enrol failed: {enrol.Error}");
        var confirmed = await identity.ConfirmTotpAsync(id, TestTotp.Now(secret), CancellationToken.None);
        if (!confirmed.IsSuccess) throw new InvalidOperationException($"TOTP confirm failed: {confirmed.Error}");
        var login = await identity.LoginAsync(new LoginRequest(email, password, TestTotp.Now(secret)), PartitionTag(), CancellationToken.None);
        if (login.Session is null) throw new InvalidOperationException($"Admin login failed: {login.Error}");
        return new TestUser(id, email, password, login.Session.AccessToken);
    }

    /// <summary>An Admin with no authenticator: login yields only the 10-minute enrol-only token (spec §7.4).</summary>
    public async Task<TestUser> CreateAdminEnrolOnlyAsync()
    {
        var (id, email, password) = await RegisterConfirmedAsync("admin-enrol", starter: true);
        using var scope = factory.Services.CreateScope();
        await SetRoleAsync(scope, id, "Admin");
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var login = await identity.LoginAsync(new LoginRequest(email, password, null), PartitionTag(), CancellationToken.None);
        var token = login.Enrol?.EnrolToken ?? throw new InvalidOperationException($"Expected an enrol-only token: {login.Error}");
        return new TestUser(id, email, password, token);
    }

    public async Task<string> CreateApiKeyAsync(TestUser owner, params string[] scopes)
    {
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var created = await identity.CreateApiKeyAsync(owner.Id, $"qa-{TestIps.UniqueSuffix()}", scopes, CancellationToken.None);
        if (!created.IsSuccess) throw new InvalidOperationException($"API key create failed: {created.Error}");
        if (!created.Value!.Key.Scopes.OrderBy(s => s).SequenceEqual(scopes.OrderBy(s => s)))
            throw new InvalidOperationException("API key was created with different scopes than requested.");
        return created.Value.Secret;
    }

    /// <summary>Creates a link through LinkService.CreateAsync (the real create path, minus HTTP and the
    /// per-user link-write limiter, so fixtures do not use up a test's own rate-limit budget).</summary>
    public async Task<LinkDto> CreateLinkAsync(TestUser owner, LinkInput input)
    {
        using var scope = factory.Services.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<LinkService>();
        var result = await links.CreateAsync(owner.Id, input, CancellationToken.None);
        return result.IsSuccess ? result.Value! : throw new InvalidOperationException($"Link create failed: {result.Error}");
    }

    public Task<LinkDto> CreateLinkAsync(TestUser owner, string? destination = null) =>
        CreateLinkAsync(owner, new LinkInput(destination ?? $"https://example.com/qa/{TestIps.UniqueSuffix()}"));

    /// <summary>Creates a link through POST /api/links on the API host (the full HTTP path).</summary>
    public async Task<LinkDto> CreateLinkViaHttpAsync(TestUser owner, LinkInput input)
    {
        using var client = factory.ApiClient(bearer: owner.AccessToken);
        var response = await client.PostAsJsonAsync("/api/links", input, Json);
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.Created) throw new InvalidOperationException($"POST /api/links -> {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<LinkDto>(body, Json)!;
    }

    // ---- internals ------------------------------------------------------------------------

    private static string PartitionTag() => $"qa:{TestIps.UniqueSuffix()}";

    private async Task<(Guid Id, string Email, string Password)> RegisterConfirmedAsync(string tag, bool starter)
    {
        var email = $"qa-api-{tag}-{TestIps.UniqueSuffix()}@example.test";
        var password = $"Qa-{TestIps.UniqueSuffix()}-pw!";
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var registered = await identity.RegisterAsync(new RegisterRequest(email, password, $"QA {tag}"), CancellationToken.None);
        if (registered.Error is not null || registered.DevOnlyConfirmToken is null)
            throw new InvalidOperationException($"Register failed: {registered.Error}");

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var confirmed = await identity.ConfirmEmailAsync(id, registered.DevOnlyConfirmToken, CancellationToken.None);
        if (!confirmed.IsSuccess) throw new InvalidOperationException($"Confirm failed: {confirmed.Error}");

        if (starter)
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            billing.Subscriptions.Add(new Subscription { UserId = id, Tier = PlanTier.Starter, Status = "qa-test" });
            await billing.SaveChangesAsync();
        }
        return (id, email, password);
    }

    private static async Task SetRoleAsync(IServiceScope scope, Guid userId, string role)
    {
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.Role = role;
        await db.SaveChangesAsync();
    }
}

/// <summary>RFC 6238 (SHA-1, 6 digits, 30 s), the same parameters as the product's Totp class, written
/// independently so the test does not depend on the product's internal ComputeCode.</summary>
public static class TestTotp
{
    public static string Now(string base32Secret)
    {
        var key = FromBase32(base32Secret);
        var counter = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() / 30;
        Span<byte> c = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { c[i] = (byte)(counter & 0xFF); counter >>= 8; }
        var hash = HMACSHA1.HashData(key, c);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static byte[] FromBase32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in s.TrimEnd('=').ToUpperInvariant())
        {
            var idx = alphabet.IndexOf(ch);
            if (idx < 0) continue;
            buffer = (buffer << 5) | idx;
            bits += 5;
            if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return output.ToArray();
    }
}
