using System.Globalization;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Http;

/// <summary>A non-success API answer. <see cref="CliException.Code"/> is the problem <c>code</c>, normalised to the
/// hyphen form (the API mixes <c>plan_required</c> and <c>insufficient-scope</c>; problem <c>type</c> URLs always use
/// hyphens, ApiResults.ProblemType).</summary>
public sealed class ApiException(int exitCode, string message, string code, int status, TimeSpan? retryAfter)
    : CliException(exitCode, message, code)
{
    public int Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>CLI-003: problem <c>code</c> (falling back to HTTP status) to exit code and message. Clients branch on
/// <c>code</c>, never on status alone (DESKTOP_CLI_PLAN.md A4).</summary>
public static class ErrorMapper
{
    public const string ProblemTypeBase = "https://zaa.gl/problems/";

    public static string Normalize(string code) => code.Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>The problem code: the <c>code</c> extension, else the last segment of a zaa.gl <c>type</c>, else one
    /// derived from the status (same table as ApiResults.CodeForStatus).</summary>
    public static string CodeFor(ProblemDto? problem, int status)
    {
        if (!string.IsNullOrWhiteSpace(problem?.Code)) return Normalize(problem.Code);
        if (problem?.Type is { } type && type.StartsWith(ProblemTypeBase, StringComparison.Ordinal) && type.Length > ProblemTypeBase.Length)
            return Normalize(type[ProblemTypeBase.Length..]);
        return status switch
        {
            400 => "validation",
            401 => "unauthorized",
            402 => "plan-required",
            403 => "forbidden",
            404 => "not-found",
            409 => "conflict",
            410 => "expired",
            413 => "payload-too-large",
            415 => "unsupported-media-type",
            429 => "too-many-requests",
            503 => "service-unavailable",
            >= 500 => "internal-error",
            _ => "error",
        };
    }

    public static int ExitCodeFor(string code, int status) => Normalize(code) switch
    {
        "unauthorized" or "invalid-code" or "insufficient-scope" or "forbidden" or "refresh-raced" => ExitCodes.Auth,
        "plan-required" or "quota-exceeded" or "quota" or "insufficient-storage" or "expiry-beyond-plan" => ExitCodes.Plan,
        "not-found" or "gone" or "expired" or "deleted" or "disabled" or "burned" or "taken-down" => ExitCodes.NotFound,
        "too-many-requests" or "rate-limit" => ExitCodes.RateLimited,
        "service-unavailable" or "busy" or "guest-creation-paused" or "internal-error" => ExitCodes.Network,
        "validation" or "snippet-too-large" or "file-too-large" or "payload-too-large" or "unsupported-media-type"
            or "idempotency-mismatch" or "captcha-required" or "captcha-invalid" or "conflict" or "content-link"
            or "version-conflict" or "not-active" => ExitCodes.Validation,
        _ => status switch
        {
            401 or 403 => ExitCodes.Auth,
            402 => ExitCodes.Plan,
            404 or 410 => ExitCodes.NotFound,
            429 => ExitCodes.RateLimited,
            400 or 409 or 413 or 415 or 422 => ExitCodes.Validation,
            >= 500 => ExitCodes.Network,
            _ => ExitCodes.Unexpected,
        },
    };

    /// <summary>Builds the user-facing message: the server's title/detail (sanitised for the terminal) plus a hint.</summary>
    public static ApiException ToException(ProblemDto? problem, int status, TimeSpan? retryAfter)
    {
        var code = CodeFor(problem, status);
        var exit = ExitCodeFor(code, status);
        var text = TextFormat.Safe(problem?.Detail);
        if (text.Length == 0) text = TextFormat.Safe(problem?.Title);
        if (text.Length == 0) text = $"The server answered {status.ToString(CultureInfo.InvariantCulture)}.";

        var hint = code switch
        {
            "insufficient-scope" => " The API key does not have the scope this command needs.",
            "unauthorized" or "invalid-code" => " Run 'zaagl login' to sign in again.",
            "plan-required" => " This needs a paid plan.",
            _ when exit == ExitCodes.RateLimited && retryAfter is { } ra =>
                $" Try again in {Math.Ceiling(ra.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.",
            _ => "",
        };
        return new ApiException(exit, text.TrimEnd() + hint, code, status, retryAfter);
    }

    public static CliException Network(string what) =>
        new(ExitCodes.Network, $"Could not reach the Zaagl API ({what}). Check your connection and try again.", "network");
}
