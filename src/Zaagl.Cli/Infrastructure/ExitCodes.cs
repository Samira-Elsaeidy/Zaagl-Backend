namespace Zaagl.Cli.Infrastructure;

/// <summary>Process exit codes (DESKTOP_CLI_PLAN.md §4). Scripts depend on these numbers; never renumber.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Unexpected error (bug, unknown server answer).</summary>
    public const int Unexpected = 1;
    /// <summary>Bad command line.</summary>
    public const int Usage = 2;
    /// <summary>Not signed in, credentials rejected, session revoked, or missing scope.</summary>
    public const int Auth = 3;
    /// <summary>Plan or quota: <c>plan_required</c>, quota exceeded, expiry beyond the plan, daily limit.</summary>
    public const int Plan = 4;
    /// <summary>Not found or gone.</summary>
    public const int NotFound = 5;
    /// <summary>Network failure or server unavailable (retryable).</summary>
    public const int Network = 6;
    /// <summary>Rate limited.</summary>
    public const int RateLimited = 7;
    /// <summary>Validation failure or input too large.</summary>
    public const int Validation = 8;
    /// <summary>Interrupted (Ctrl+C).</summary>
    public const int Interrupted = 130;
}

/// <summary>An error the CLI reports to the user and turns into an exit code. Messages are user-facing.</summary>
public class CliException(int exitCode, string message, string? code = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    /// <summary>Machine-readable error code for <c>--json</c> output (problem <c>code</c> or a CLI code).</summary>
    public string Code { get; } = code ?? DefaultCode(exitCode);

    public static string DefaultCode(int exitCode) => exitCode switch
    {
        ExitCodes.Usage => "usage",
        ExitCodes.Auth => "unauthorized",
        ExitCodes.Plan => "plan-required",
        ExitCodes.NotFound => "not-found",
        ExitCodes.Network => "network",
        ExitCodes.RateLimited => "rate-limit",
        ExitCodes.Validation => "validation",
        ExitCodes.Interrupted => "interrupted",
        _ => "error",
    };

    public static CliException Usage(string message) => new(ExitCodes.Usage, message, "usage");

    public static CliException NotSignedIn() =>
        new(ExitCodes.Auth, "You are not signed in. Run 'zaagl login' first.", "not-signed-in");
}
