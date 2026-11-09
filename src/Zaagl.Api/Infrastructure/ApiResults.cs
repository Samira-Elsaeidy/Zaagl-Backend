using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Infrastructure;

public static class ApiResults
{
    /// <summary>BE-002 / spec §7.9: every problem's <c>type</c> is <c>https://zaa.gl/problems/&lt;code&gt;</c>.</summary>
    public const string ProblemTypeBase = "https://zaa.gl/problems/";

    /// <summary>tus 1.0 checksum extension: the chunk did not match its <c>Upload-Checksum</c>.</summary>
    public const int Status460ChecksumMismatch = 460;

    public static string ProblemType(string code) => ProblemTypeBase + code.Replace('_', '-');

    public static int StatusFor(string code) => code switch
    {
        ErrorCodes.Validation or ErrorCodes.CaptchaRequired or ErrorCodes.CaptchaInvalid
            or ErrorCodes.UploadLengthRequired or ErrorCodes.ExpiryBeyondPlan => StatusCodes.Status400BadRequest,
        ErrorCodes.Unauthorized or ErrorCodes.InvalidCode or ErrorCodes.PasswordRequired => StatusCodes.Status401Unauthorized,
        ErrorCodes.PlanRequired => StatusCodes.Status402PaymentRequired,
        ErrorCodes.Forbidden or ErrorCodes.InsufficientScope or ErrorCodes.QuotaExceeded
            or ErrorCodes.TicketInvalid => StatusCodes.Status403Forbidden,
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.Conflict or ErrorCodes.UploadOffsetMismatch or ErrorCodes.UploadIncomplete or ErrorCodes.AlreadyFinalized
            or ErrorCodes.Processing or ErrorCodes.BurnAlreadyClaimed or ErrorCodes.NotActive
            or ErrorCodes.ContentLink or ErrorCodes.VersionConflict => StatusCodes.Status409Conflict,
        // Spec §7.4: a parallel refresh from the same session; the caller retries with its newer token.
        ErrorCodes.RefreshRaced => StatusCodes.Status409Conflict,
        ErrorCodes.Expired or ErrorCodes.UploadExpired or ErrorCodes.Burned or ErrorCodes.Deleted or ErrorCodes.Disabled
            or ErrorCodes.TakenDown or ErrorCodes.Gone => StatusCodes.Status410Gone,
        ErrorCodes.LengthRequired => StatusCodes.Status411LengthRequired,
        ErrorCodes.RangeRequired => StatusCodes.Status416RangeNotSatisfiable,
        ErrorCodes.InsufficientStorage => StatusCodes.Status507InsufficientStorage,
        ErrorCodes.FileTooLarge or ErrorCodes.ChunkTooLarge or ErrorCodes.SnippetTooLarge
            or ErrorCodes.DiffTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorCodes.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        ErrorCodes.IdempotencyMismatch or ErrorCodes.ContentRejected => StatusCodes.Status422UnprocessableEntity,
        ErrorCodes.TooManyRequests or ErrorCodes.Quota or ErrorCodes.RateLimit => StatusCodes.Status429TooManyRequests,
        ErrorCodes.ChecksumMismatch => Status460ChecksumMismatch,
        ErrorCodes.ServiceUnavailable or ErrorCodes.GuestCreationPaused or ErrorCodes.Busy => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>Seconds a client waits before retrying a <see cref="ErrorCodes.Busy"/> answer (spec §7.0).</summary>
    public const int BusyRetryAfterSeconds = 1;

    public static IResult Problem(Error error)
    {
        var problem = Results.Problem(
            statusCode: StatusFor(error.Code),
            title: error.Message,
            type: ProblemType(error.Code),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
        return error.Code == ErrorCodes.Busy ? new WithRetryAfter(problem, BusyRetryAfterSeconds) : problem;
    }

    private sealed class WithRetryAfter(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>For problems not built by <see cref="Problem"/> (framework 4xx/5xx written through
    /// <c>IProblemDetailsService</c>): give them a zaa.gl <c>type</c> and a <c>code</c> derived from the status, so
    /// every problem body has <c>type</c>, <c>title</c>, <c>status</c> and <c>code</c>.</summary>
    public static void Normalize(ProblemDetails problem)
    {
        if (problem.Extensions.TryGetValue("code", out var existing) && existing is string code && code.Length > 0)
        {
            if (problem.Type is null || !problem.Type.StartsWith(ProblemTypeBase, StringComparison.Ordinal))
                problem.Type = ProblemType(code);
            return;
        }

        code = CodeForStatus(problem.Status ?? StatusCodes.Status500InternalServerError);
        problem.Extensions["code"] = code;
        problem.Type = ProblemType(code);
    }

    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.Validation,
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => "method-not-allowed",
        StatusCodes.Status409Conflict => ErrorCodes.Conflict,
        StatusCodes.Status410Gone => ErrorCodes.Expired,
        StatusCodes.Status413PayloadTooLarge => "payload-too-large",
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.UnsupportedMediaType,
        StatusCodes.Status429TooManyRequests => ErrorCodes.TooManyRequests,
        StatusCodes.Status503ServiceUnavailable => ErrorCodes.ServiceUnavailable,
        >= 500 => "internal-error",
        _ => "error",
    };

    public static IResult From<T>(Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess
            ? (onSuccess ?? (v => Results.Ok(v)))(result.Value!)
            : Problem(result.Error!);

    public static IResult NotFound(string message = "Not found.") => Problem(Error.NotFound(message));

    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal has no user id.");
    }
}
