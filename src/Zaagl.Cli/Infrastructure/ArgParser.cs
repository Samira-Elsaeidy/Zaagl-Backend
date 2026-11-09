namespace Zaagl.Cli.Infrastructure;

/// <summary>One option a command accepts. <paramref name="Name"/> is the long name without dashes.</summary>
public sealed record OptionSpec(string Name, bool TakesValue, char? Short = null, string? Help = null);

public sealed class ParsedArgs
{
    public string? Command { get; set; }
    public List<string> Positionals { get; } = [];
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public bool Has(string name) => Flags.Contains(name) || Values.ContainsKey(name);
    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Small hand-rolled parser (DESKTOP_CLI_PLAN.md §4 allows it when System.CommandLine is not AOT-clean; it also keeps
/// the binary free of package dependencies). Grammar: <c>zaagl [global options] command [args and options]</c>.
/// Supports <c>--name value</c>, <c>--name=value</c>, <c>-x</c>, <c>-x value</c> and <c>--</c> to end options.
/// Global options are accepted before and after the command name.
/// </summary>
public static class ArgParser
{
    public static readonly IReadOnlyList<OptionSpec> GlobalOptions =
    [
        new("json", false, null, "Print one JSON object (JSON Lines for ls) on stdout."),
        new("quiet", false, 'q', "Print only the result (for scripts)."),
        new("no-color", false, null, "Do not use colours (NO_COLOR is honoured too)."),
        new("api-url", true, null, "API base URL (default https://api.zaa.gl)."),
        new("help", false, 'h', "Show help."),
        new("version", false, null, "Show the version."),
    ];

    /// <param name="commandOptions">Options of a command, or null when the command is unknown (then only global
    /// options are accepted and the caller reports the unknown command).</param>
    public static ParsedArgs Parse(IReadOnlyList<string> args, Func<string, IReadOnlyList<OptionSpec>?> commandOptions)
    {
        var result = new ParsedArgs();
        IReadOnlyList<OptionSpec> specific = [];
        var optionsEnded = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (optionsEnded || token == "-" || !token.StartsWith('-'))
            {
                if (result.Command is null && !optionsEnded)
                {
                    result.Command = token;
                    specific = commandOptions(token) ?? [];
                }
                else
                {
                    result.Positionals.Add(token);
                }
                continue;
            }

            if (token == "--")
            {
                optionsEnded = true;
                continue;
            }

            string name;
            string? inlineValue = null;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var body = token[2..];
                var eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    inlineValue = body[(eq + 1)..];
                    body = body[..eq];
                }
                name = body;
            }
            else
            {
                if (token.Length != 2)
                    throw CliException.Usage($"Unknown option '{token}'. Short options cannot be combined.");
                var spec0 = Find(specific, token[1]) ?? Find(GlobalOptions, token[1])
                    ?? throw CliException.Usage($"Unknown option '{token}'.");
                name = spec0.Name;
            }

            var spec = Find(specific, name) ?? Find(GlobalOptions, name)
                ?? throw CliException.Usage(result.Command is null
                    ? $"Unknown option '--{name}'."
                    : $"Unknown option '--{name}' for '{result.Command}'. See 'zaagl {result.Command} --help'.");

            if (!spec.TakesValue)
            {
                if (inlineValue is not null)
                    throw CliException.Usage($"Option '--{spec.Name}' does not take a value.");
                result.Flags.Add(spec.Name);
                continue;
            }

            string value;
            if (inlineValue is not null)
            {
                value = inlineValue;
            }
            else
            {
                if (i + 1 >= args.Count)
                    throw CliException.Usage($"Option '--{spec.Name}' needs a value.");
                value = args[++i];
            }

            if (!result.Values.TryAdd(spec.Name, value))
                throw CliException.Usage($"Option '--{spec.Name}' was given more than once.");
        }

        return result;
    }

    private static OptionSpec? Find(IReadOnlyList<OptionSpec> specs, string name)
    {
        foreach (var s in specs)
            if (s.Name == name) return s;
        return null;
    }

    private static OptionSpec? Find(IReadOnlyList<OptionSpec> specs, char shortName)
    {
        foreach (var s in specs)
            if (s.Short == shortName) return s;
        return null;
    }
}
