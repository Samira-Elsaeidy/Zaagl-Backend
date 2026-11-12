using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Domain;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary>Values of <c>media_derivatives.status</c>; the same numbers as the Media contracts' DerivativeStatus.</summary>
public enum DerivativeState : short { Pending = 0, Ready = 1, Failed = 2, Skipped = 3 }

/// <summary>Limits how many images are probed and thumbnailed at once in this process (<c>Media:MaxParallel</c>).</summary>
public sealed class MediaConcurrencyGate(IOptions<MediaOptions> options)
{
    public SemaphoreSlim Slots { get; } = new(Math.Max(1, options.Value.MaxParallel));
}

/// <summary>
/// The worker task <c>media-preview</c> (ARC-011 sections 2, 3.1 and 6), queued by the scan task after <c>ready</c> for media
/// resources. It reads the promoted original by path (read only: invariant I1; it never touches <c>storage_objects.sha256</c>),
/// writes the probe result to <c>files.media_items</c> and the <c>thumb-320</c> file to <c>thumbnails/</c> with its own id. A bad,
/// oversized or undecodable image ends as a <c>failed</c> or <c>skipped</c> derivative row and a successful task: the resource
/// stays ready and its original is served (I4). Only infrastructure errors (database, disk) fail the task and retry.
/// Idempotent: a media row plus a derivative row means done. Burn files, rejected, deleted and not-yet-clean resources get nothing (I5).
/// </summary>
public sealed class MediaPreviewTaskHandler(
    FilesDbContext db,
    IResourceRegistry resources,
    LocalObjectStorage storage,
    MediaConcurrencyGate gate,
    TimeProvider time,
    IOptions<MediaOptions> options,
    ILogger<MediaPreviewTaskHandler> logger) : ITaskHandler<FilesDbContext>
{
    public const string TaskKind = "media-preview";
    public string Kind => TaskKind;

    public async Task HandleAsync(ClaimedTask task, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(task.Payload);
        var resourceId = doc.RootElement.GetProperty("resourceId").GetGuid();
        var o = options.Value;

        var resource = await resources.GetAsync(resourceId, ct);
        if (!Eligible(resource)) return;
        var obj = await db.StorageObjects.AsNoTracking().FirstOrDefaultAsync(x => x.ResourceId == resourceId, ct);
        // Only a clean, promoted original of an allowlisted image type is ever opened (derivatives exist only when the scan is clean).
        if (obj is not { Area: StorageArea.Objects, ScanStatus: ScanVerdict.Clean } || !MediaSniffer.IsMediaAllowed(obj.DetectedMime)) return;

        var hasMedia = await db.MediaItems.AnyAsync(m => m.ResourceId == resourceId, ct);
        var hasThumb = await db.MediaDerivatives.AnyAsync(d => d.ResourceId == resourceId && d.Kind == DerivativeKindThumb, ct);
        if (hasMedia && hasThumb) return;

        var path = storage.ResolvePath(StorageKeys.Create(StorageArea.Objects, obj.Id));
        if (!File.Exists(path)) return;

        await gate.Slots.WaitAsync(ct);
        ImageHeader? header = null;
        ThumbnailResult thumb;
        try
        {
            // Probe and decode run on the thread pool with a wall-clock limit; a decoder that hangs is abandoned, never awaited forever.
            var work = Task.Run(() =>
            {
                var h = ImageProbe.Read(path);
                var t = h is null ? ThumbnailResult.Fail("unprobeable") : ImageThumbnailer.Create(path, h, o);
                return (h, t);
            }, CancellationToken.None);
            try
            {
                (header, thumb) = await work.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)), ct);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("media_probe_timeout: resource {ResourceId}", resourceId);
                thumb = ThumbnailResult.Fail("timeout");
            }
        }
        finally
        {
            gate.Slots.Release();
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (!hasMedia)
        {
            var orientation = thumb.Orientation;
            db.MediaItems.Add(new MediaItemRow
            {
                ResourceId = resourceId,
                MediaType = (short)(header?.Animated == true ? 1 : 0),
                Width = header is null ? null : checked((int)header.Width),
                Height = header is null ? null : checked((int)header.Height),
                Orientation = header is null ? null : (short)orientation,
                FrameCount = header?.FrameCount,
                HasAlpha = header?.HasAlpha,
                ProbedAt = now,
            });
        }

        var derivativeId = Guid.NewGuid();
        string? writtenKey = null;
        if (!hasThumb)
        {
            var row = new MediaDerivativeRow
            {
                Id = derivativeId, ResourceId = resourceId, Kind = DerivativeKindThumb, Area = StorageArea.Thumbnails, CreatedAt = now,
            };
            if (thumb.Outcome == ThumbnailOutcome.Created)
            {
                using var bytes = new MemoryStream(thumb.Bytes!, writable: false);
                var put = await storage.PutAsync(StorageArea.Thumbnails, bytes, ct);
                writtenKey = put.StorageKey;
                row.Id = StorageKeys.Parse(put.StorageKey).Id;
                row.Status = (short)DerivativeState.Ready;
                row.Width = thumb.Width;
                row.Height = thumb.Height;
                row.SizeBytes = put.SizeBytes;
                row.Mime = thumb.Mime;
            }
            else
            {
                row.Status = (short)(thumb.Outcome == ThumbnailOutcome.Skipped ? DerivativeState.Skipped : DerivativeState.Failed);
                row.Reason = thumb.Reason?[..Math.Min(thumb.Reason.Length, 64)];
            }
            db.MediaDerivatives.Add(row);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Either a parallel run of the same task won the insert (then this is a duplicate), or the database failed (retry).
            if (writtenKey is not null) await storage.DeleteAsync(writtenKey, CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.MediaItems.AsNoTracking().AnyAsync(m => m.ResourceId == resourceId, CancellationToken.None)) return;
            throw;
        }
        catch
        {
            if (writtenKey is not null) await storage.DeleteAsync(writtenKey, CancellationToken.None);
            throw;
        }

        // The resource may have been deleted while the image was processed: a derivative never outlives its resource (I6). The delete-bytes
        // task removes the object row last, so a missing row means the bytes are gone and this derivative would be an orphan.
        if (writtenKey is not null
            && (!await db.StorageObjects.AsNoTracking().AnyAsync(x => x.Id == obj.Id, ct) || !Eligible(await resources.GetAsync(resourceId, ct))))
        {
            await storage.DeleteAsync(writtenKey, ct);
            await db.MediaDerivatives.Where(d => d.ResourceId == resourceId).ExecuteDeleteAsync(ct);
            await db.MediaItems.Where(m => m.ResourceId == resourceId).ExecuteDeleteAsync(ct);
        }
    }

    public const string DerivativeKindThumb = "thumb-320";

    private static bool Eligible(ResourceDescriptor? r) =>
        r is { Kind: ResourceKind.Media, BurnAfterDownload: false, Processing: ProcessingStatus.Ready, State: ResourceState.Ready };
}

/// <summary>Reads for the owner and public descriptors (<see cref="IMediaMetadata"/>).</summary>
public sealed class MediaMetadata(FilesDbContext db) : IMediaMetadata
{
    public async Task<IReadOnlyDictionary<Guid, StoredMedia>> GetAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
    {
        if (resourceIds.Count == 0) return new Dictionary<Guid, StoredMedia>();
        var items = await db.MediaItems.AsNoTracking().Where(m => resourceIds.Contains(m.ResourceId)).ToListAsync(ct);
        var derivatives = (await db.MediaDerivatives.AsNoTracking().Where(d => resourceIds.Contains(d.ResourceId)).ToListAsync(ct))
            .ToLookup(d => d.ResourceId);
        return items.ToDictionary(m => m.ResourceId, m =>
        {
            var swap = m.Orientation is >= 5;
            return new StoredMedia(
                m.ResourceId, m.MediaType == 1, swap ? m.Height : m.Width, swap ? m.Width : m.Height, m.Orientation, m.FrameCount, m.HasAlpha,
                derivatives[m.ResourceId].OrderBy(d => d.Kind, StringComparer.Ordinal).Select(ToDerivative).ToList());
        });
    }

    public async Task<StoredDerivative?> GetReadyDerivativeAsync(Guid resourceId, string kind, CancellationToken ct)
    {
        var d = await db.MediaDerivatives.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ResourceId == resourceId && x.Kind == kind && x.Status == (short)DerivativeState.Ready, ct);
        return d is null ? null : ToDerivative(d);
    }

    private static StoredDerivative ToDerivative(MediaDerivativeRow d) => new(
        d.Id, d.Kind, ((DerivativeState)d.Status).ToString().ToLowerInvariant(), d.Width, d.Height, d.Mime, d.SizeBytes);
}

/// <summary>Worker only, once per process start: queues <c>media-preview</c> for clean image originals that have no media row
/// (ARC-011 3.1: "BE-015 backfills"; the probe is idempotent, so a repeat is harmless). It walks the rows by creation time with a
/// cursor, so resources the task later declines (files, burn) cannot starve the rest. New uploads are queued by the scan task.</summary>
public sealed class MediaBackfillJob(IServiceScopeFactory scopes, IOptions<MediaOptions> options, TimeProvider time, ILogger<MediaBackfillJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let the API's migrations finish first
            DateTime? cursor = null;
            var total = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = scopes.CreateScope();
                var (queued, next) = await EnqueueBatchAsync(scope.ServiceProvider.GetRequiredService<FilesDbContext>(),
                    scope.ServiceProvider.GetRequiredKeyedService<TaskQueue>(FilesDbContext.Schema), cursor, stoppingToken);
                if (next is null) break;
                cursor = next;
                total += queued;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            if (total > 0) logger.LogInformation("Queued {Count} media-preview backfill tasks.", total);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The media backfill sweep failed; it runs again at the next worker start.");
        }
    }

    /// <returns>How many tasks were queued and the cursor for the next batch (null when there was nothing left).</returns>
    internal async Task<(int Queued, DateTime? Next)> EnqueueBatchAsync(FilesDbContext db, TaskQueue tasks, DateTime? after, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var floor = after ?? DateTime.MinValue;
        var rows = await db.StorageObjects.AsNoTracking()
            .Where(o => o.CreatedAt > floor && o.Area == StorageArea.Objects && o.ScanStatus == ScanVerdict.Clean
                && o.DetectedMime != null && MediaSniffer.MediaAllowlist.Contains(o.DetectedMime)
                && !db.MediaItems.Any(m => m.ResourceId == o.ResourceId))
            .OrderBy(o => o.CreatedAt).Select(o => new { o.ResourceId, o.CreatedAt }).Take(Math.Max(1, options.Value.BackfillBatch)).ToListAsync(ct);
        var queued = 0;
        foreach (var row in rows)
            if (await tasks.EnqueueAsync(db, MediaPreviewTaskHandler.TaskKind, $"media-preview:{row.ResourceId}",
                    JsonSerializer.Serialize(new { resourceId = row.ResourceId }), Math.Max(1, options.Value.MaxAttempts), now, now, ct) is not null) queued++;
        return (queued, rows.Count == 0 ? null : rows[^1].CreatedAt);
    }
}
