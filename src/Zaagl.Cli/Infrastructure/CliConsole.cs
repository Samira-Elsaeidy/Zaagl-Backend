using System.Text;

namespace Zaagl.Cli.Infrastructure;

/// <summary>The three standard streams plus redirection facts. Fakes replace it in tests.</summary>
public interface ICliConsole
{
    /// <summary>Results only (the link, JSON). Scripts read this.</summary>
    TextWriter Out { get; }

    /// <summary>Everything else: prompts, progress, warnings, errors.</summary>
    TextWriter Error { get; }

    bool IsOutputRedirected { get; }
    bool IsErrorRedirected { get; }
    bool IsInputRedirected { get; }

    /// <summary>Raw stdin bytes (the CLI decodes them itself, see <see cref="InputDecoder"/>).</summary>
    Stream OpenStandardInput();

    /// <summary>Raw stderr bytes, for <c>paste --tee</c>.</summary>
    Stream OpenStandardError();
}

public sealed class SystemConsole : ICliConsole
{
    public SystemConsole()
    {
        // When a stream is redirected, write UTF-8 (no BOM) so pipes and files get UTF-8 whatever the console code
        // page is. When it is a terminal, keep Console's writer: on Windows it writes UTF-16 to the console API, and
        // changing Console.OutputEncoding would change the parent shell's code page after we exit.
        Out = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : Console.Out;
        Error = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : Console.Error;
    }

    public TextWriter Out { get; }
    public TextWriter Error { get; }
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    public bool IsErrorRedirected => Console.IsErrorRedirected;
    public bool IsInputRedirected => Console.IsInputRedirected;
    public Stream OpenStandardInput() => Console.OpenStandardInput();
    public Stream OpenStandardError() => Console.OpenStandardError();

    private static StreamWriter Utf8Writer(Stream s) =>
        new(s, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
}
