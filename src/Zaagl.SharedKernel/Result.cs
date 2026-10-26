namespace Zaagl.SharedKernel;

public sealed record Error(string Code, string Message)
{
    public static Error NotFound(string message = "Not found.") => new("not_found", message);
    public static Error Conflict(string message) => new("conflict", message);
    public static Error Validation(string message) => new("validation", message);
    public static Error Unauthorized(string message = "Unauthorized.") => new("unauthorized", message);
    public static Error Forbidden(string message) => new("forbidden", message);
    public static Error PlanRequired(string message) => new("plan_required", message);
    public static Error ServiceUnavailable(string message) => new("service_unavailable", message);
}

public readonly record struct Result<T>(T? Value, Error? Error)
{
    public bool IsSuccess => Error is null;

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(Error error) => new(default, error);

    public static implicit operator Result<T>(Error error) => Fail(error);
    public static implicit operator Result<T>(T value) => Ok(value);
}
