using System.Security.Claims;
using Zaagl.SharedKernel;

namespace Zaagl.Api.Infrastructure;

public static class ApiResults
{
    public static IResult Problem(Error error)
    {
        var status = error.Code switch
        {
            "validation" => StatusCodes.Status400BadRequest,
            "unauthorized" => StatusCodes.Status401Unauthorized,
            "plan_required" => StatusCodes.Status402PaymentRequired,
            "forbidden" => StatusCodes.Status403Forbidden,
            "not_found" => StatusCodes.Status404NotFound,
            "conflict" => StatusCodes.Status409Conflict,
            "expired" => StatusCodes.Status410Gone,
            "too_many_requests" => StatusCodes.Status429TooManyRequests,
            "service_unavailable" or "guest_creation_paused" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Problem(
            statusCode: status,
            title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

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
