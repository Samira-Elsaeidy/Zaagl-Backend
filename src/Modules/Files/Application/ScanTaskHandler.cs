using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Domain;
using Zaagl.Modules.Files.Infrastructure;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary>Limits how many scans run at once in this process (<c>Scan:Concurrency</c>).</summary>
public sealed class ScanConcurrencyGate(IOptions<ScanOptions> options)
{
    public SemaphoreSlim Slots { get; } = new(Math.Max(1, options.Value.Concurrency));
}

/// <summary>
/// The worker task <c>scan</c> (ARC-009 section 7): identify, hash, blocked-hash check, malware scan, promote or quarantine. Every
/// step re-reads state and does nothing when its effect already happened, so a killed worker or a retried task is safe. The
/// original bytes are only ever read; <c>Ready</c> is set after the rename and the area update, so the authorize hop never
/// reads a key that does not exist yet.
/// </summary>
public sealed class ScanTaskHandler(
    FilesDbContext db,
    IResourceRegistry resources,
    IContentHashBlocklist blocklist,
    IMalwareScanner scanner,
    LocalObjectStorage storage,
    ScanConcurrencyGate gate,
    [FromKeyedServices(FilesDbContext.Schema)] TaskQueue tasks,
    TimeProvider time,
    IOptions<ScanOptions> scanOptions,
    ILogger<ScanTaskHandler> logger) : ITaskHandler<FilesDbContext>
{
    public const string TaskKind = "scan";
    private const int HeadBytes = MediaSniffer.HeadBytes;

    public string Kind => TaskKind;

    public TimeSpan RetryDelay(int attempt)
    {
        var minutes = scanOptions.Value.RetryMinutes is { Length: > 0 } m ? m[Math.Clamp(attempt - 1, 0, m.Length - 1)] : 1;
        return TimeSpan.FromMinutes(Math.Max(0, minutes));
    }

    public async Task HandleAsync(ClaimedTask task, CancellationToken ct)
    {
        var resourceId = ReadResourceId(task);

        // 0. Load. A resource that is rejected, deleted, disabled or purgeable needs nothing.
        var obj = await db.StorageObjects.AsNoTracking().FirstOrDefaultAsync(o => o.ResourceId == resourceId, ct);
        if (obj is null) { logger.LogError("Scan task for resource {ResourceId} has no storage object", resourceId); return; }
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null) return;
        if (resource.State is ResourceState.Rejected or ResourceState.Deleted or ResourceState.Disabled or ResourceState.Purgeable) return;
        if (obj is { Area: StorageArea.Objects, ScanStatus: ScanVerdict.Clean } && resource.Processing == ProcessingStatus.Ready) return;

        await gate.Slots.WaitAsync(ct);
        try
        {
            await ScanAsync(obj, resource, ct);
        }
        finally
        {
            gate.Slots.Release();
        }
    }

    private async Task ScanAsync(StorageObjectRow obj, ResourceDescriptor resource, CancellationToken ct)
    {
        var resourceId = obj.ResourceId;
        var tempKey = StorageKeys.Create(StorageArea.Temp, obj.Id);
        var objectKey = StorageKeys.Create(StorageArea.Objects, obj.Id);

        // 1. Pending to Scanning (no-op when already Scanning or Error).
        await resources.MarkScanningAsync(resourceId, ct);

        // 2. Locate the file: temp, else objects (a previous attempt promoted and then crashed).
        string currentKey;
        long? size = await storage.StatAsync(tempKey, ct);
        if (size is not null) currentKey = tempKey;
        else if ((size = await storage.StatAsync(objectKey, ct)) is not null) currentKey = objectKey;
        else
        {
            logger.LogError("files_object_missing: resource {ResourceId} has no bytes in temp or objects", resourceId);
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.ScanUnavailable, ct);
            return;
        }
        var promoted = currentKey == objectKey;

        // 3. Identify and hash in one sequential read; persist the hash and the detected type.
        byte[] sha256;
        DetectedType? detected;
        long counted;
        if (obj.Sha256 is { } known && promoted && obj.ScanStatus == ScanVerdict.Clean)
        {
            sha256 = known;
            counted = size!.Value;
            detected = obj.DetectedMime is null ? null : new DetectedType(obj.DetectedMime, obj.DetectedExt ?? "");
        }
        else
        {
            (sha256, detected, counted) = await HashAsync(storage.ResolvePath(currentKey), ct);
            if (counted != obj.SizeBytes)
            {
                // Over the plan maximum: reject. Otherwise it is not what was uploaded: an error, retried.
                if (counted > obj.SizeBytes)
                {
                    await DeleteTempAsync(tempKey, ct);
                    await resources.MarkRejectedAsync(resourceId, RejectedReasons.TooLarge, ct);
                    return;
                }
                throw new InvalidOperationException($"The stored file has {counted} bytes, the upload declared {obj.SizeBytes}.");
            }
            await db.StorageObjects.Where(o => o.Id == obj.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Sha256, sha256)
                .SetProperty(o => o.DetectedMime, detected?.Mime)
                .SetProperty(o => o.DetectedExt, detected?.Extension), ct);
        }

        // 4. Category (media allowlist for Media) and the blocked-hash list.
        if (resource.Kind == ResourceKind.Media && !MediaSniffer.IsMediaAllowed(detected?.Mime))
        {
            await DeleteTempAsync(tempKey, ct);
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.TypeNotAllowed, ct);
            return;
        }
        if (await blocklist.IsBlockedAsync(sha256, ct))
        {
            var key = await QuarantineAsync(obj.Id, currentKey, ScanVerdict.Pending, null, ct);
            await resources.MarkRejectedAsync(resourceId, RejectedReasons.BlockedHash, new RejectionEvidence(sha256, key), ct);
            return;
        }

        // 5. Scan (skipped when an earlier attempt already recorded Clean and only the promote is missing).
        if (obj.ScanStatus != ScanVerdict.Clean)
        {
            await using var stream = new FileStream(storage.ResolvePath(currentKey), new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            var result = await scanner.ScanAsync(stream, storage.ResolvePath(currentKey), ct);
            var now = time.GetUtcNow().UtcDateTime;
            switch (result.Kind)
            {
                case ScanOutcomeKind.Found:
                {
                    var key = await QuarantineAsync(obj.Id, currentKey, ScanVerdict.Infected, result, ct);
                    await resources.MarkRejectedAsync(resourceId, RejectedReasons.Infected, new RejectionEvidence(sha256, key), ct);
                    return;
                }
                case ScanOutcomeKind.Error:
                    await db.StorageObjects.Where(o => o.Id == obj.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.ScanStatus, ScanVerdict.Error).SetProperty(o => o.ScanEngine, result.Engine), ct);
                    // Nothing is served, not even to the owner. The task is retried; the last failure rejects it (OnDead).
                    throw new InvalidOperationException("The malware scan did not complete: " + result.Reason);
                default:
                    await db.StorageObjects.Where(o => o.Id == obj.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.ScanStatus, ScanVerdict.Clean)
                        .SetProperty(o => o.ScanEngine, result.Engine)
                        .SetProperty(o => o.ScannedAt, now), ct);
                    break;
            }
        }

        // 6a. Promote: one atomic rename keeping the id, then the area, then Ready (quota commit).
        if (!promoted)
        {
            var current = await storage.StatAsync(tempKey, ct);
            if (current != obj.SizeBytes) throw new InvalidOperationException("The temp file changed size before promote.");
            await storage.MoveAsync(tempKey, StorageArea.Objects, ct);
        }
        await db.StorageObjects.Where(o => o.Id == obj.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Area, StorageArea.Objects), ct);
        var ready = await resources.MarkReadyAsync(resourceId, obj.SizeBytes, ct);
        if (!ready.IsSuccess) throw new InvalidOperationException("Could not mark the resource ready: " + ready.Error!.Message);

        // 7. Derivatives and metadata (ARC-011): queued after ready, for media resources only (never burn files); the task re-checks.
        // A failure here never fails the scan: the original is already servable and the backfill sweep catches a missed task.
        if (resource.Kind == ResourceKind.Media && !resource.BurnAfterDownload)
        {
            try
            {
                var now = time.GetUtcNow().UtcDateTime;
                await tasks.EnqueueAsync(db, MediaPreviewTaskHandler.TaskKind, $"media-preview:{resourceId}",
                    JsonSerializer.Serialize(new { resourceId }), 3, now, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not queue media-preview for resource {ResourceId}", resourceId);
            }
        }
    }

    /// <summary>The dead-letter path (retries exhausted): rejected as scan-unavailable, temp bytes deleted, quota released.</summary>
    public async Task OnDeadAsync(ClaimedTask task, CancellationToken ct)
    {
        var resourceId = ReadResourceId(task);
        await resources.MarkRejectedAsync(resourceId, RejectedReasons.ScanUnavailable, ct);
        var objectId = await db.StorageObjects.AsNoTracking().Where(o => o.ResourceId == resourceId).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (objectId is { } id) await DeleteTempAsync(StorageKeys.Create(StorageArea.Temp, id), ct);
    }

    private static async Task<(byte[] Sha256, DetectedType? Detected, long Length)> HashAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var head = new byte[HeadBytes];
        var headLength = 0;
        long total = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            if (headLength < HeadBytes)
            {
                var take = Math.Min(read, HeadBytes - headLength);
                Buffer.BlockCopy(buffer, 0, head, headLength, take);
                headLength += take;
            }
            total += read;
        }
        return (hash.GetHashAndReset(), MediaSniffer.Detect(head.AsSpan(0, headLength)), total);
    }

    /// <summary>6b: the bytes move to quarantine (rename, id kept), the row follows, then Resources is told.</summary>
    private async Task<string> QuarantineAsync(Guid objectId, string currentKey, ScanVerdict verdict, ScanResult? result, CancellationToken ct)
    {
        var key = await storage.MoveAsync(currentKey, StorageArea.Quarantine, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var signature = result?.Signature;
        var engine = result?.Engine;
        DateTime? scannedAt = result is null ? null : now;
        await db.StorageObjects.Where(o => o.Id == objectId).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.Area, StorageArea.Quarantine)
            .SetProperty(o => o.ScanStatus, verdict)
            .SetProperty(o => o.ScanSignature, signature)
            .SetProperty(o => o.ScanEngine, engine)
            .SetProperty(o => o.ScannedAt, scannedAt), ct);
        return key;
    }

    private async Task DeleteTempAsync(string tempKey, CancellationToken ct) => await storage.DeleteAsync(tempKey, ct);

    private static Guid ReadResourceId(ClaimedTask task)
    {
        using var doc = JsonDocument.Parse(task.Payload);
        return doc.RootElement.GetProperty("resourceId").GetGuid();
    }
}
