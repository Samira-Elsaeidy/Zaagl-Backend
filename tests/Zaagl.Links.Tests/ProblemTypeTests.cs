using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Zaagl.Api.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Links.Tests;

/// <summary>BE-002 / spec §7.9: every problem response has <c>type</c> = https://zaa.gl/problems/&lt;code&gt;,
/// <c>title</c>, <c>status</c> and <c>code</c>; 410, 413, 415 and 460 are supported.</summary>
public sealed class ProblemTypeTests
{
    public static TheoryData<string> AllCodes()
    {
        var data = new TheoryData<string>();
        foreach (var f in typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
            data.Add((string)f.GetValue(null)!);
        return data;
    }

    private static async Task<(int Status, JsonElement Body, string? ContentType)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging()
            .AddProblemDetails(o => o.CustomizeProblemDetails = ctx => ApiResults.Normalize(ctx.ProblemDetails))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, JsonDocument.Parse(http.Response.Body).RootElement.Clone(), http.Response.ContentType);
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public async Task Every_error_code_produces_a_complete_problem_with_a_zaagl_type(string code)
    {
        var (status, body, contentType) = await ExecuteAsync(ApiResults.Problem(new Error(code, "Something specific.")));

        Assert.Equal(ApiResults.StatusFor(code), status);
        Assert.Equal("https://zaa.gl/problems/" + code.Replace('_', '-'), body.GetProperty("type").GetString());
        Assert.Equal("Something specific.", body.GetProperty("title").GetString());
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.StartsWith("application/problem+json", contentType);
    }

    [Theory]
    [InlineData(ErrorCodes.Expired, 410)]
    [InlineData(ErrorCodes.Burned, 410)]
    [InlineData(ErrorCodes.UploadExpired, 410)]
    [InlineData(ErrorCodes.FileTooLarge, 413)]
    [InlineData(ErrorCodes.ChunkTooLarge, 413)]
    [InlineData(ErrorCodes.SnippetTooLarge, 413)]
    [InlineData(ErrorCodes.UnsupportedMediaType, 415)]
    [InlineData(ErrorCodes.ChecksumMismatch, 460)]
    [InlineData(ErrorCodes.IdempotencyMismatch, 422)]
    [InlineData(ErrorCodes.ContentRejected, 422)]
    [InlineData(ErrorCodes.PasswordRequired, 401)]
    [InlineData(ErrorCodes.TicketInvalid, 403)]
    [InlineData(ErrorCodes.QuotaExceeded, 403)]
    [InlineData(ErrorCodes.UploadOffsetMismatch, 409)]
    [InlineData(ErrorCodes.BurnAlreadyClaimed, 409)]
    [InlineData(ErrorCodes.Quota, 429)]
    [InlineData(ErrorCodes.InsufficientScope, 403)]
    [InlineData(ErrorCodes.RefreshRaced, 409)]
    [InlineData(ErrorCodes.PlanRequired, 402)]
    // C9 catalogue (contract freeze).
    [InlineData(ErrorCodes.Deleted, 410)]
    [InlineData(ErrorCodes.Disabled, 410)]
    [InlineData(ErrorCodes.TakenDown, 410)]
    [InlineData(ErrorCodes.Gone, 410)]
    [InlineData(ErrorCodes.RangeRequired, 416)]
    [InlineData(ErrorCodes.VersionConflict, 409)]
    [InlineData(ErrorCodes.DiffTooLarge, 413)]
    [InlineData(ErrorCodes.LengthRequired, 411)]
    [InlineData(ErrorCodes.Busy, 503)]
    [InlineData(ErrorCodes.InsufficientStorage, 507)]
    [InlineData(ErrorCodes.ContentLink, 409)]
    [InlineData("some_unknown_code", 400)] // unchanged default
    public void Status_per_code(string code, int status) => Assert.Equal(status, ApiResults.StatusFor(code));

    [Fact]
    public async Task Busy_carries_Retry_After()
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await ApiResults.Problem(Error.Of(ErrorCodes.Busy, "Try again.")).ExecuteAsync(http);
        Assert.Equal(503, http.Response.StatusCode);
        Assert.Equal("1", http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task A_framework_problem_without_a_code_gets_one_and_a_zaagl_type()
    {
        var (status, body, _) = await ExecuteAsync(Results.Problem(statusCode: 415));
        Assert.Equal(415, status);
        Assert.Equal("https://zaa.gl/problems/unsupported-media-type", body.GetProperty("type").GetString());
        Assert.Equal(ErrorCodes.UnsupportedMediaType, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("title").GetString()));
    }

    [Fact]
    public void Normalize_replaces_a_foreign_type_but_keeps_an_existing_code()
    {
        var problem = new ProblemDetails { Status = 409, Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10" };
        problem.Extensions["code"] = ErrorCodes.RefreshRaced;
        ApiResults.Normalize(problem);
        Assert.Equal("https://zaa.gl/problems/refresh-raced", problem.Type);
        Assert.Equal(ErrorCodes.RefreshRaced, problem.Extensions["code"]);
    }

    [Fact]
    public async Task The_real_app_returns_a_zaagl_type()
    {
        await using var api = new RealApiFactory();
        using var client = api.ApiClient();
        var response = await client.PostAsync("/hubs/analytics/negotiate?negotiateVersion=1&ticket=" + new string('b', 43), null);
        // 401 challenge has no body; use a route with a problem body instead.
        var notFound = await client.PostAsync("/api/auth/external/exchange", JsonContent("""{"code":"nope"}"""));
        var body = JsonDocument.Parse(await notFound.Content.ReadAsStringAsync()).RootElement;
        Assert.StartsWith("https://zaa.gl/problems/", body.GetProperty("type").GetString());
        Assert.True(body.TryGetProperty("code", out _));
        Assert.Equal((int)notFound.StatusCode, body.GetProperty("status").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
