using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zaagl.Cli.Infrastructure;

public enum OutputMode
{
    Human,
    Json,
    Quiet,
}

/// <summary>
/// CLI-002 output rules: stdout carries only the result (the link is the last stdout line, so
/// <c>url=$(zaagl paste &lt; x)</c> works); messages, progress and errors go to stderr. <c>--json</c> prints one JSON
/// object (JSON Lines for lists); <c>--quiet</c> prints only the result and suppresses informational messages.
/// </summary>
public sealed class Output(ICliConsole console, OutputMode mode, bool color)
{
    public OutputMode Mode { get; } = mode;
    public bool Color { get; } = color;
    public ICliConsole Console { get; } = console;

    /// <summary>A result line on stdout (human and quiet modes).</summary>
    public void Result(string line) => Console.Out.WriteLine(line);

    /// <summary>One JSON value on one stdout line.</summary>
    public void Json<T>(T value, JsonTypeInfo<T> typeInfo) => Console.Out.WriteLine(JsonSerializer.Serialize(value, typeInfo));

    /// <summary>Informational message on stderr; suppressed by <c>--quiet</c> and <c>--json</c>.</summary>
    public void Info(string message)
    {
        if (Mode == OutputMode.Human) Console.Error.WriteLine(message);
    }

    /// <summary>A warning on stderr; suppressed only by <c>--quiet</c>.</summary>
    public void Warn(string message)
    {
        if (Mode != OutputMode.Quiet) Console.Error.WriteLine(Paint("warning: ", "33") + message);
    }

    /// <summary>An error on stderr (always), plus a JSON error object on stdout in <c>--json</c> mode.</summary>
    public void Fail(CliException error)
    {
        Console.Error.WriteLine(Paint("error: ", "31") + error.Message);
        if (Mode == OutputMode.Json)
            Json(new ErrorEnvelope(new ErrorBody(error.Code, error.Message, error.ExitCode)), CliJson.Context.ErrorEnvelope);
    }

    /// <summary>Progress and prompts belong on stderr only when a person is watching it.</summary>
    public bool CanShowProgress => Mode == OutputMode.Human && !Console.IsErrorRedirected;

    private string Paint(string text, string sgr) => Color ? $"\u001b[{sgr}m{text}\u001b[0m" : text;

    /// <summary>Colour is used only on an interactive stderr, never with NO_COLOR, <c>--no-color</c>,
    /// <c>color=never</c> or TERM=dumb. On Windows only in terminals known to process VT sequences.</summary>
    public static bool ResolveColor(string? setting, bool noColorFlag, Func<string, string?> env, bool stderrRedirected)
    {
        if (noColorFlag || !string.IsNullOrEmpty(env("NO_COLOR"))) return false;
        if (setting == "never") return false;
        if (setting == "always") return true;
        if (stderrRedirected || env("TERM") == "dumb") return false;
        if (OperatingSystem.IsWindows())
            return !string.IsNullOrEmpty(env("WT_SESSION")) || !string.IsNullOrEmpty(env("TERM"));
        return true;
    }
}

public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(string Code, string Message, int ExitCode);
