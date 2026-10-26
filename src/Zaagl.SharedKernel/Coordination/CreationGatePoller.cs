using Microsoft.Extensions.Hosting;

namespace Zaagl.SharedKernel.Coordination;

/// <summary>
/// Polls the coordinator's key-fingerprint status at startup and periodically thereafter, so a
/// coordinator that is slow to come up (or briefly unreachable) doesn't permanently fault creation -
/// the API retries with backoff until a match succeeds (spec §5.4).
/// </summary>
public sealed class CreationGatePoller(CreationGate gate) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            await gate.PollOnceAsync(stoppingToken);
            delay = gate.CanCreate ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
