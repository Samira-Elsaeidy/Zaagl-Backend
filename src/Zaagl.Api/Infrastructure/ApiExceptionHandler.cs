using Microsoft.AspNetCore.Diagnostics;
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// The last line of defence for an exception that escaped an endpoint. The body is always a zaa.gl problem+json built here, never the
/// framework's: no exception type, message or stack trace is written to the response in any environment (they are logged by the
/// exception-handler middleware). A coordinator that cannot lease is a 503 with Retry-After; anything else is a generic 500.
/// </summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (http.Response.HasStarted) return false;
        var error = exception switch
        {
            CoordinatorUnavailableException => Error.ServiceUnavailable("Creating links is temporarily unavailable. Please try again in a moment."),
            // Request binding failures (a number where a string is expected, an array where a scalar is, a number that overflows its
            // type, malformed JSON, a bad route or query value) are the caller's mistake: 400 invalid_request, never a 500.
            // Minimal APIs throw these when RouteHandlerOptions.ThrowOnBadRequest is on (Program.cs), so every one lands here.
            BadHttpRequestException bad => ForBadRequest(bad),
            System.Text.Json.JsonException json => Error.Of(ErrorCodes.InvalidRequest, BodyMessage(json)),
            _ => Error.Of("internal-error", "Something went wrong on our side. Please try again."),
        };
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.Remove("Content-Length");
        var status = exception is BadHttpRequestException { StatusCode: >= 400 and < 500 } bhre ? bhre.StatusCode
            : error.Code == "internal-error" ? StatusCodes.Status500InternalServerError : ApiResults.StatusFor(error.Code);
        if (status == StatusCodes.Status503ServiceUnavailable)
            http.Response.Headers.RetryAfter = ApiResults.UnavailableRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        await http.Response.WriteAsJsonAsync(new Dictionary<string, object?>
        {
            ["type"] = ApiResults.ProblemType(error.Code),
            ["title"] = error.Message,
            ["status"] = status,
            ["code"] = error.Code,
        }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
        return true;
    }

    private static Error ForBadRequest(BadHttpRequestException bad)
    {
        if (bad.StatusCode == StatusCodes.Status400BadRequest) return Error.Of(ErrorCodes.InvalidRequest, BodyMessage(bad));
        return Error.Of(ApiResults.CodeForStatus(bad.StatusCode), "The request could not be processed.");
    }

    /// <summary>A stable, safe sentence. The JSON path of the offending property (<c>$.destinationUrl</c>) is the only detail taken from the exception
    /// and only after it is restricted to identifier characters; the framework's message and the exception type are never echoed.</summary>
    private static string BodyMessage(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Text.Json.JsonException { Path: { Length: > 0 } path } && SafePath(path) is { } field)
                return $"The value for \"{field}\" is not valid: it has the wrong type or is out of range.";
        }
        return "The request is not valid: the body, a query value or a route value is malformed, has the wrong type or is out of range.";
    }

    private static string? SafePath(string path)
    {
        var p = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
        if (p.Length is 0 or > 120) return null;
        foreach (var c in p)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '[' or ']')) return null;
        return p;
    }
}
