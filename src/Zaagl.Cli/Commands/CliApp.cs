using System.Globalization;
using Zaagl.Cli.Auth;
using Zaagl.Cli.Config;
using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Commands;

/// <summary>Everything the CLI touches outside the process, so tests can substitute each piece (CLI-002).</summary>
public sealed class CliServices
{
    public required ICliConsole Console { get; init; }
    public required Func<string, string?> Env { get; init; }
    public required ICredentialStore Store { get; init; }
    public required ICrossProcessLock Lock { get; init; }
    public required IBrowserLauncher Browser { get; init; }
    public required string ConfigDirectory { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (d, ct) => Task.Delay(d, ct);

    /// <summary>Null means the production SocketsHttpHandler.</summary>
    public Func<HttpMessageHandler>? HttpHandler { get; init; }

    public static CliServices CreateDefault()
    {
        Func<string, string?> env = Environment.GetEnvironmentVariable;
        var dir = ConfigPaths.GetConfigDirectory(env);
        return new CliServices
        {
            Console = new SystemConsole(),
            Env = env,
            Store = CredentialStores.CreateDefault(),
            Lock = new FileCrossProcessLock(dir),
            Browser = new SystemBrowserLauncher(),
            ConfigDirectory = dir,
        };
    }
}

/// <summary>What a command handler gets. The API client and token manager are built on first use so commands that
/// never touch the network (config, help) do not create an HttpClient.</summary>
public sealed class CommandContext(Output output, ParsedArgs args, CliServices services, CliConfig config, Uri apiBase)
{
    private ApiClient? _api;
    private TokenManager? _tokens;

    public Output Output { get; } = output;
    public ParsedArgs Args { get; } = args;
    public CliServices Services { get; } = services;
    public CliConfig Config { get; } = config;
    public Uri ApiBase { get; } = apiBase;

    public string StoreKey => CredentialStores.KeyFor(ApiBase);

    private HttpClient? _http;

    private ApiClient AnonymousClient() =>
        new(_http ??= ApiClient.CreateHttpClient(Services.HttpHandler?.Invoke()), ApiBase, null, Services.Delay);

    public TokenManager Tokens => _tokens ??= new TokenManager(
        Services.Store, StoreKey, Services.Lock, AnonymousClient(), Services.Env, Services.Time, Services.Delay);

    public ApiClient Api => _api ??= new ApiClient(
        _http ??= ApiClient.CreateHttpClient(Services.HttpHandler?.Invoke()), ApiBase, Tokens, Services.Delay);

    /// <summary>For unauthenticated calls (device flow, logout).</summary>
    public ApiClient Anonymous => AnonymousClient();
}

public delegate Task<int> CommandHandler(CommandContext context, CancellationToken ct);

public sealed record CommandSpec(string Name, string Summary, string Usage, IReadOnlyList<OptionSpec> Options, CommandHandler Handler);

/// <summary>CLI-002: parse, resolve output mode, run the command, map every failure to an exit code. stdout holds only
/// the result; everything else is stderr.</summary>
public static class CliApp
{
    public static IReadOnlyList<CommandSpec> Commands { get; } =
    [
        LoginCommand.Spec, LogoutCommand.Spec, WhoamiCommand.Spec, LinkCommand.Spec, PasteCommand.Spec,
        LsCommand.Spec, ConfigCommand.Spec,
        new("version", "Show the version.", "zaagl version", [], VersionHandler),
    ];

    private static CommandSpec? Find(string name) => Commands.FirstOrDefault(c => c.Name == name);

    public static async Task<int> RunAsync(string[] args, CliServices services, CancellationToken ct)
    {
        var console = services.Console;
        // Output is created before parsing so even a usage error honours --json / NO_COLOR as far as they are known.
        var early = new Output(console, args.Contains("--json") ? OutputMode.Json : args.Contains("--quiet") || args.Contains("-q") ? OutputMode.Quiet : OutputMode.Human,
            Output.ResolveColor(null, args.Contains("--no-color"), services.Env, console.IsErrorRedirected));
        try
        {
            var parsed = ArgParser.Parse(args, name => Find(name)?.Options);

            if (parsed.Has("version")) return VersionOutput(early);
            if (parsed.Command is null || parsed.Command == "help" || parsed.Has("help"))
            {
                var topic = parsed.Command == "help" ? parsed.Positionals.FirstOrDefault() : parsed.Command;
                console.Out.Write(Help(topic));
                return parsed.Command is null && !parsed.Has("help") ? ExitCodes.Usage : ExitCodes.Ok;
            }

            var spec = Find(parsed.Command)
                ?? throw CliException.Usage($"Unknown command '{parsed.Command}'. Run 'zaagl --help' for the list.");

            var config = CliConfig.Load(services.ConfigDirectory);
            var mode = parsed.Has("json") ? OutputMode.Json
                : parsed.Has("quiet") ? OutputMode.Quiet
                : config.Get(ConfigKeys.Output) switch { "json" => OutputMode.Json, "quiet" => OutputMode.Quiet, _ => OutputMode.Human };
            var output = new Output(console, mode,
                Output.ResolveColor(config.Get(ConfigKeys.Color), parsed.Has("no-color"), services.Env, console.IsErrorRedirected));

            var ctx = new CommandContext(output, parsed, services, config, ResolveApiBase(parsed, config, services.Env));
            try
            {
                return await spec.Handler(ctx, ct).ConfigureAwait(false);
            }
            catch (CliException ex)
            {
                output.Fail(ex);
                return ex.ExitCode;
            }
        }
        catch (CliException ex)
        {
            early.Fail(ex);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            console.Error.WriteLine("Interrupted.");
            return ExitCodes.Interrupted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            early.Fail(new CliException(ExitCodes.Unexpected, "Unexpected error: " + TextFormat.Safe(ex.Message), "unexpected"));
            return ExitCodes.Unexpected;
        }
    }

    internal static Uri ResolveApiBase(ParsedArgs parsed, CliConfig config, Func<string, string?> env)
    {
        var value = parsed.Get("api-url");
        if (value is null && env("ZAAGL_API_URL") is { Length: > 0 } fromEnv) value = fromEnv;
        value ??= config.Get(ConfigKeys.ApiUrl) ?? ConfigKeys.DefaultApiUrl;
        if (ApiUrlRules.Validate(value) is { } problem) throw CliException.Usage("Invalid API URL: " + problem);
        return ApiUrlRules.Normalize(value);
    }

    private static Task<int> VersionHandler(CommandContext ctx, CancellationToken ct)
    {
        VersionOutput(ctx.Output);
        return Task.FromResult(ExitCodes.Ok);
    }

    private static int VersionOutput(Output output)
    {
        if (output.Mode == OutputMode.Json)
            output.Json(new VersionResult(PlatformInfo.Version, PlatformInfo.OsName, PlatformInfo.Arch), CliJson.Context.VersionResult);
        else
            output.Result(output.Mode == OutputMode.Quiet ? PlatformInfo.Version : $"zaagl {PlatformInfo.Version} ({PlatformInfo.OsName} {PlatformInfo.Arch})");
        return ExitCodes.Ok;
    }

    public static string Help(string? topic)
    {
        var sb = new System.Text.StringBuilder();
        if (topic is not null && Find(topic) is { } c)
        {
            sb.Append("Usage: ").AppendLine(c.Usage).AppendLine().AppendLine(c.Summary);
            if (c.Options.Count > 0)
            {
                sb.AppendLine().AppendLine("Options:");
                foreach (var o in c.Options) AppendOption(sb, o);
            }
        }
        else
        {
            sb.AppendLine("zaagl - share links, snippets and terminal output from the command line.").AppendLine()
                .AppendLine("Usage: zaagl [options] <command> [arguments]").AppendLine().AppendLine("Commands:");
            foreach (var cmd in Commands) sb.Append("  ").Append(cmd.Name.PadRight(8)).AppendLine(cmd.Summary);
            sb.AppendLine().AppendLine("Global options:");
            foreach (var o in ArgParser.GlobalOptions) AppendOption(sb, o);
            sb.AppendLine().AppendLine("Exit codes: 0 ok, 1 unexpected, 2 usage, 3 not signed in, 4 plan or quota, 5 not found,")
                .AppendLine("6 network, 7 rate limited, 8 validation or too large, 130 interrupted.");
        }
        return sb.ToString();
    }

    private static void AppendOption(System.Text.StringBuilder sb, OptionSpec o)
    {
        var left = (o.Short is { } s ? $"-{s}, " : "    ") + "--" + o.Name + (o.TakesValue ? " <value>" : "");
        sb.Append("  ").Append(left.PadRight(28)).AppendLine(o.Help ?? "");
    }

    internal static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
