using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Analytics.Domain;

namespace Zaagl.Modules.Analytics.Infrastructure;

/// <summary>In-memory buffer between the redirect endpoint and the database writer.</summary>
public sealed class ClickChannel
{
    private readonly Channel<ClickEvent> _channel = Channel.CreateBounded<ClickEvent>(
        new BoundedChannelOptions(100_000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false instead of blocking when full
        });

    public ChannelReader<ClickEvent> Reader => _channel.Reader;
    public ChannelWriter<ClickEvent> Writer => _channel.Writer;
}

public sealed class ClickRecorder(ClickChannel channel, ILogger<ClickRecorder> logger) : IClickRecorder
{
    public bool TryRecord(ClickEvent click)
    {
        if (channel.Writer.TryWrite(click)) return true;
        logger.LogWarning("Click buffer full; dropping click for link {LinkId}", click.LinkId);
        return false;
    }
}

/// <summary>
/// Drains the click buffer in batches into PostgreSQL, then pushes human clicks to live dashboards.
/// Buffered ingestion is acceptable for click analytics; strict counters live elsewhere (see ILinkResolver).
/// </summary>
public sealed class ClickWriterService(
    ClickChannel channel,
    IServiceScopeFactory scopes,
    ILogger<ClickWriterService> logger) : BackgroundService
{
    private const int MaxBatch = 500;
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClickEvent>(MaxBatch);
        try
        {
            while (await channel.Reader.WaitToReadAsync(stoppingToken))
            {
                Drain(batch);
                if (batch.Count < MaxBatch)
                {
                    await Task.Delay(Linger, stoppingToken); // let a burst accumulate into one insert
                    Drain(batch);
                }
                await WriteAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }

        // Flush whatever is left so a clean shutdown loses nothing.
        Drain(batch);
        if (batch.Count > 0) await WriteAsync(batch, CancellationToken.None);
    }

    private void Drain(List<ClickEvent> batch)
    {
        while (batch.Count < MaxBatch && channel.Reader.TryRead(out var click)) batch.Add(click);
    }

    private async Task WriteAsync(List<ClickEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                db.ClickEvents.AddRange(batch.Select(c => new ClickEventRow
                {
                    LinkId = c.LinkId,
                    OccurredAt = c.OccurredAt,
                    VisitorHash = c.VisitorHash,
                    ReferrerHost = c.ReferrerHost,
                    Country = c.Country,
                    City = c.City,
                    Os = c.Os,
                    Browser = c.Browser,
                    DeviceType = c.DeviceType,
                    IsBot = c.IsBot,
                }));
                await db.SaveChangesAsync(ct);

                await PublishLiveAsync(scope.ServiceProvider, batch, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Writing {Count} clicks failed (attempt {Attempt}/3)", batch.Count, attempt);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
        logger.LogError("Dropped {Count} clicks after repeated write failures", batch.Count);
    }

    private async Task PublishLiveAsync(IServiceProvider services, List<ClickEvent> batch, CancellationToken ct)
    {
        var publisher = services.GetService<ILiveClickPublisher>();
        if (publisher is null) return;

        var live = batch.Where(c => !c.IsBot)
            .Select(c => new LiveClick(c.LinkId, c.OccurredAt, c.ReferrerHost, c.Country, c.City, c.Os, c.Browser, c.DeviceType))
            .ToList();
        if (live.Count == 0) return;

        try
        {
            await publisher.PublishAsync(live, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Live click publish failed");
        }
    }
}

/// <summary>Keeps upcoming monthly partitions of click_events in place (the migration creates the first ones).</summary>
public sealed class PartitionMaintenanceService(IServiceScopeFactory scopes, ILogger<PartitionMaintenanceService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
            do
            {
                await EnsureAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT analytics.ensure_click_partitions(3)", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ensuring click_events partitions failed");
        }
    }
}
