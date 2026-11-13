using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links.Application;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Tests;

/// <summary>The harness runs with the no-op verifier; this factory swaps in <see cref="ScriptedCaptchaVerifier"/>, whose answer
/// depends on the token, so the CAPTCHA gate on the public auth routes can be exercised end to end.</summary>
public sealed class CaptchaApiFactory : ZaaglApiFactory
{
    public ScriptedCaptchaVerifier Captcha { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaVerifier>();
            services.AddSingleton<ICaptchaVerifier>(Captcha);
        });
    }
}

/// <summary>Mirrors <c>TurnstileVerifier</c>'s error shapes: no token -> captcha-required, "bad" -> captcha-invalid,
/// "down" -> service_unavailable, anything else passes. Every call is recorded (token, action, hostname).</summary>
public sealed class ScriptedCaptchaVerifier : ICaptchaVerifier
{
    public const string Bad = "bad";
    public const string Down = "down";

    public ConcurrentQueue<(string? Token, string Action, string Hostname)> Calls { get; } = new();

    public Task<Result<bool>> VerifyAsync(string? token, string expectedAction, string expectedHostname, CancellationToken ct)
    {
        Calls.Enqueue((token, expectedAction, expectedHostname));
        Result<bool> result = token switch
        {
            null or "" => Error.Validation("captcha-required"),
            Bad => Error.Validation("captcha-invalid"),
            Down => Error.ServiceUnavailable("Could not verify the CAPTCHA right now. Try again shortly."),
            _ => Result<bool>.Ok(true),
        };
        return Task.FromResult(result);
    }
}

/// <summary>Spec §7.12 / §7.4: register, resend-confirmation and forgot-password need a <c>Captcha-Token</c>. The check runs before
/// any account work, so a refused request looks the same for a known and an unknown address, and creates nothing.</summary>
public sealed class AuthCaptchaTests(CaptchaApiFactory api) : IClassFixture<CaptchaApiFactory>
{
    private static string NewEmail(string tag) => $"qa-api-captcha-{tag}-{TestIps.UniqueSuffix()}@example.test";

    private static string Password() => $"Qa-{TestIps.UniqueSuffix()}-pw!";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? captchaToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (captchaToken is not null) request.Headers.Add("Captcha-Token", captchaToken);
        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage r, HttpStatusCode status, string code, string? title = null)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {(int)status}, got {(int)r.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        if (title is not null) Assert.Equal(title, body.GetProperty("title").GetString());
    }

    private async Task<bool> UserExistsAsync(string email)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var normalized = email.ToLowerInvariant();
        return await db.Users.AnyAsync(u => u.Email.ToLower() == normalized);
    }

    public static TheoryData<string, string> Routes => new()
    {
        { "/api/auth/register", "register" },
        { "/api/auth/resend-confirmation", "resend-confirmation" },
        { "/api/auth/forgot-password", "forgot-password" },
    };

    public static TheoryData<string> Paths => new() { "/api/auth/register", "/api/auth/resend-confirmation", "/api/auth/forgot-password" };

    private static object BodyFor(string path, string email) =>
        path == "/api/auth/register" ? new RegisterRequest(email, Password(), "QA captcha") : new { email };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Missing_token_is_400_captcha_required_and_identical_for_known_and_unknown_addresses(string path)
    {
        var existing = await api.Principals.CreateUserAsync(tag: "captcha-known");
        using var client = api.ApiClient();

        var known = await PostAsync(client, path, BodyFor(path, existing.Email), captchaToken: null);
        var unknown = await PostAsync(client, path, BodyFor(path, NewEmail("unknown")), captchaToken: null);

        await AssertProblemAsync(known, HttpStatusCode.BadRequest, "validation", "captcha-required");
        await AssertProblemAsync(unknown, HttpStatusCode.BadRequest, "validation", "captcha-required");
        Assert.Equal(StripTraceId(await known.Content.ReadAsStringAsync()), StripTraceId(await unknown.Content.ReadAsStringAsync()));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Rejected_token_is_400_captcha_invalid(string path)
    {
        using var client = api.ApiClient();
        var r = await PostAsync(client, path, BodyFor(path, NewEmail("bad")), ScriptedCaptchaVerifier.Bad);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, "validation", "captcha-invalid");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Verifier_outage_is_503_never_a_silent_pass(string path)
    {
        using var client = api.ApiClient();
        var r = await PostAsync(client, path, BodyFor(path, NewEmail("down")), ScriptedCaptchaVerifier.Down);
        await AssertProblemAsync(r, HttpStatusCode.ServiceUnavailable, "service_unavailable");
        Assert.True(r.Headers.Contains("Retry-After"));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Valid_token_is_verified_for_the_route_action_and_frontend_host_and_answers_202(string path, string action)
    {
        using var client = api.ApiClient();
        var token = "ok-" + TestIps.UniqueSuffix();
        var r = await PostAsync(client, path, BodyFor(path, NewEmail("ok")), token);

        Assert.True(r.StatusCode == HttpStatusCode.Accepted, $"{path} -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        var call = Assert.Single(api.Captcha.Calls, c => c.Token == token);
        Assert.Equal(action, call.Action);
        Assert.Equal(new Uri(ZaaglApiFactory.WebBase).Host, call.Hostname);
    }

    [Fact]
    public async Task Register_without_a_valid_token_creates_no_account()
    {
        using var client = api.ApiClient();
        var missing = NewEmail("nouser-missing");
        var bad = NewEmail("nouser-bad");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/auth/register", BodyFor("/api/auth/register", missing), null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/auth/register", BodyFor("/api/auth/register", bad), ScriptedCaptchaVerifier.Bad)).StatusCode);

        Assert.False(await UserExistsAsync(missing));
        Assert.False(await UserExistsAsync(bad));
    }

    [Fact]
    public async Task Login_does_not_require_a_captcha()
    {
        var user = await api.Principals.CreateUserAsync(tag: "captcha-login");
        using var client = api.ApiClient();
        var login = await PostAsync(client, "/api/auth/login", new LoginRequest(user.Email, user.Password, null), captchaToken: null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>Problem bodies may carry a per-request traceId; everything else must match byte for byte.</summary>
    private static string StripTraceId(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"traceId\"\\s*:\\s*\"[^\"]*\"", "\"traceId\":\"\"");
}
