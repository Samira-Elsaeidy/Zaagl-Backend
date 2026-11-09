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
    public static Error TooManyRequests(string message) => new("too_many_requests", message);

    /// <summary>An error with one of the stable <see cref="ErrorCodes"/> (BE-002); the HTTP status and the
    /// RFC 7807 <c>type</c> follow from the code.</summary>
    public static Error Of(string code, string message) => new(code, message);
}

/// <summary>
/// BE-002: stable, machine-readable error codes. Each becomes the problem's <c>code</c> extension and its RFC 7807
/// <c>type</c> <c>https://zaa.gl/problems/&lt;code&gt;</c> (underscores written as hyphens). Codes never change once
/// shipped; clients (web, CLI, desktop) switch on them. The HTTP status for each is in <c>ApiResults</c>.
/// </summary>
public static class ErrorCodes
{
    // Phase 0 (unchanged spelling, existing clients depend on it).
    public const string Validation = "validation";
    public const string Unauthorized = "unauthorized";
    public const string InvalidCode = "invalid_code";
    public const string PlanRequired = "plan_required";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string RefreshRaced = "refresh_raced";
    public const string Expired = "expired";
    public const string TooManyRequests = "too_many_requests";
    public const string ServiceUnavailable = "service_unavailable";
    public const string GuestCreationPaused = "guest_creation_paused";
    public const string InsufficientScope = "insufficient-scope";

    // Spec §7.2 / §8 (links), named in contracts.md.
    public const string IdempotencyMismatch = "idempotency-mismatch";
    public const string CaptchaRequired = "captcha-required";
    public const string CaptchaInvalid = "captcha-invalid";
    public const string Quota = "quota";
    public const string RateLimit = "rate-limit";

    // Phase 1 content (contracts.md "Errors", API_CONTRACTS.md uploads/resources).
    public const string FileTooLarge = "file-too-large";
    public const string ChunkTooLarge = "chunk-too-large";
    public const string SnippetTooLarge = "snippet-too-large";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string QuotaExceeded = "quota-exceeded";
    public const string UploadLengthRequired = "upload-length-required";
    public const string UploadOffsetMismatch = "upload-offset-mismatch";
    public const string UploadExpired = "upload-expired";
    public const string UploadIncomplete = "upload-incomplete";
    public const string AlreadyFinalized = "already-finalized";
    public const string ChecksumMismatch = "checksum-mismatch";
    public const string PasswordRequired = "password-required";
    public const string TicketInvalid = "ticket-invalid";
    public const string Burned = "burned";
    public const string Processing = "processing";
    public const string ContentRejected = "content-rejected";
    public const string ExpiryBeyondPlan = "expiry-beyond-plan";
    public const string BurnAlreadyClaimed = "burn-already-claimed";
    public const string NotActive = "not-active";

    /// <summary>ARC-002 §6 row 4: a Phase 0 URL-link write route was called on a content link (409); content is
    /// managed through <c>/api/resources</c>.</summary>
    public const string ContentLink = "content-link";

    // C9 error catalogue (contracts.md, ARC-004 §3, ARC-008 §4.5): closed content and delivery.
    public const string Deleted = "deleted";
    public const string Disabled = "disabled";
    public const string TakenDown = "taken-down";
    public const string Gone = "gone";
    public const string RangeRequired = "range-required";
    public const string VersionConflict = "version-conflict";
    public const string DiffTooLarge = "diff-too-large";
    public const string LengthRequired = "length-required";
    /// <summary>503 with <c>Retry-After</c>: a lock timeout (spec §7.0, ARC-006 §4); the caller retries.</summary>
    public const string Busy = "busy";
    /// <summary>507: the server's own storage is full (not the user's quota, which is <see cref="QuotaExceeded"/>).</summary>
    public const string InsufficientStorage = "insufficient-storage";
}

public readonly record struct Result<T>(T? Value, Error? Error)
{
    public bool IsSuccess => Error is null;

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(Error error) => new(default, error);

    public static implicit operator Result<T>(Error error) => Fail(error);
    public static implicit operator Result<T>(T value) => Ok(value);
}
