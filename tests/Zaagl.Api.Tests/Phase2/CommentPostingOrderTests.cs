using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Links.Application;

namespace Zaagl.Api.Tests.Phase2;

/// <summary>Comments with the scripted CAPTCHA verifier (no token: captcha-required, every call recorded).</summary>
public sealed class CommentsCaptchaFactory : Phase2MediaFactory
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

/// <summary>
/// Phase 2 bug-fixes on <c>POST /api/public/comments/{code}</c>: (1) the thread settings are checked before the CAPTCHA, so an anonymous
/// comment where anonymous posting is off is a 401 "sign in to comment" (not 400 captcha-required) and spends no CAPTCHA verification;
/// (2) x and y are required JSON numbers: a missing coordinate, a number sent as a string and NaN/Infinity are 400, never a pin at 0.
/// </summary>
[Trait("Area", "P2-Comments")]
public sealed class CommentPostingOrderTests(CommentsCaptchaFactory api) : IClassFixture<CommentsCaptchaFactory>
{
    private TestPrincipals P => api.Principals;

    private async Task<(HttpClient Owner, Guid Id, string Code, string? Key)> ImageAsync()
    {
        var owner = await P.CreateUserAsync(starter: false);
        var client = api.ApiClient(bearer: owner.AccessToken);
        var up = await MediaHelper.ReadyAsync(api, client, MediaHelper.Image(120, 80), "pin.png");
        return (client, up.ResourceId, up.Code, up.LinkKey);
    }

    private static async Task<(HttpStatusCode Status, string Code, string Body)> PostRawAsync(HttpClient client, string code, string? key, string json,
        string? captcha = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/comments/{code}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Add("X-Zaagl-Link-Key", key);
        if (captcha is not null) request.Headers.Add("Captcha-Token", captcha);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var problemCode = text.StartsWith('{') && System.Text.Json.JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? c.GetString()! : "";
        return (response.StatusCode, problemCode, text);
    }

    private int CaptchaCalls(string action) => api.Captcha.Calls.Count(c => c.Action == action);

    [Fact]
    public async Task Anonymous_posting_off_is_401_before_any_captcha_check()
    {
        var (owner, id, code, key) = await ImageAsync();
        using var anonymous = api.ApiClient();
        const string pin = """{"x":0.5,"y":0.5,"body":"hello"}""";
        var before = CaptchaCalls("guest-comment");

        // Comments off: 403, no CAPTCHA call.
        var off = await PostRawAsync(anonymous, code, key, pin);
        Assert.True(off.Status == HttpStatusCode.Forbidden, $"{(int)off.Status} {off.Body}");

        // Comments on, anonymous posting off: 401 "sign in", with or without a token, no CAPTCHA call.
        Assert.Equal(HttpStatusCode.OK, (await P2.SendAsync(owner, HttpMethod.Put, $"/api/resources/{id}/comments/settings", new { enabled = true })).Status);
        var noToken = await PostRawAsync(anonymous, code, key, pin);
        Assert.True(noToken.Status == HttpStatusCode.Unauthorized, $"{(int)noToken.Status} {noToken.Body}");
        Assert.Equal("unauthorized", noToken.Code);
        Assert.Contains("Sign in to comment", noToken.Body);
        var withToken = await PostRawAsync(anonymous, code, key, pin, captcha: "ok");
        Assert.Equal(HttpStatusCode.Unauthorized, withToken.Status);
        Assert.Equal(before, CaptchaCalls("guest-comment"));

        // Anonymous posting on: now the CAPTCHA applies (no token 400 captcha-required, a token passes).
        await P2.SendAsync(owner, HttpMethod.Put, $"/api/resources/{id}/comments/settings", new { enabled = true, allowAnonymous = true });
        var required = await PostRawAsync(anonymous, code, key, pin);
        Assert.Equal(HttpStatusCode.BadRequest, required.Status);
        Assert.Contains("captcha-required", required.Body);
        var created = await PostRawAsync(anonymous, code, key, pin, captcha: "ok");
        Assert.True(created.Status == HttpStatusCode.Created, $"{(int)created.Status} {created.Body}");
        Assert.Equal(before + 2, CaptchaCalls("guest-comment"));
    }

    [Theory]
    [InlineData("""{"x":0.5,"body":"no y"}""")]
    [InlineData("""{"y":0.5,"body":"no x"}""")]
    [InlineData("""{"body":"no coordinates"}""")]
    [InlineData("""{"x":null,"y":0.5,"body":"null x"}""")]
    [InlineData("""{"x":"0.5","y":0.5,"body":"string x"}""")]
    [InlineData("""{"x":0.5,"y":"0.5","body":"string y"}""")]
    [InlineData("""{"x":"NaN","y":0.5,"body":"nan"}""")]
    [InlineData("""{"x":"Infinity","y":0.5,"body":"inf"}""")]
    [InlineData("""{"x":1e400,"y":0.5,"body":"overflow"}""")]
    [InlineData("""{"x":1.5,"y":0.5,"body":"out of range"}""")]
    public async Task Coordinates_are_required_finite_json_numbers(string json)
    {
        var (owner, id, code, key) = await ImageAsync();
        await P2.SendAsync(owner, HttpMethod.Put, $"/api/resources/{id}/comments/settings", new { enabled = true, allowAnonymous = true });
        var signedIn = await PostRawAsync(owner, code, key, json);
        Assert.True(signedIn.Status == HttpStatusCode.BadRequest, $"{json} -> {(int)signedIn.Status} {signedIn.Body}");
        // An anonymous visitor's malformed pin is a 400 as well.
        using var anonymous = api.ApiClient();
        var anon = await PostRawAsync(anonymous, code, key, json, captcha: "ok");
        Assert.Equal(HttpStatusCode.BadRequest, anon.Status);
        Assert.DoesNotContain("captcha-required", anon.Body);

        var (_, thread) = await P2.SendAsync(owner, HttpMethod.Get, $"/api/resources/{id}/comments");
        Assert.Equal(0, thread.GetProperty("count").GetInt32());
    }
}
