using Zaagl.Cli.Commands;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the command unwind and exit 130 itself
            cts.Cancel();
        };
        try
        {
            return await CliApp.RunAsync(args, CliServices.CreateDefault(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Interrupted;
        }
    }
}
