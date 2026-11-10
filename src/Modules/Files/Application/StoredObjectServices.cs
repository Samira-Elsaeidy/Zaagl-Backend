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
using Zaagl.SharedKernel;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Application;

/// <summary>C7 read side (<see cref="IStoredObjects"/>) and the owner and viewer summaries.</summary>
public sealed class StoredObjects(
    FilesDbContext db,
    LocalObjectStorage storage,
    [FromKeyedServices(FilesDbContext.Schema)] TaskQueue tasks,
    TimeProvider time) : IStoredObjects, IStoredObjectSummaries, IUploadStats, IResourceBytes
{
    public async Task<StoredObjectInfo?> GetForResourceAsync(Guid resourceId, CancellationToken ct)
    {
        var o = await db.StorageObjects.AsNoTracking().FirstOrDefaultAsync(x => x.ResourceId == resourceId, ct);
        return o is null
            ? null
            : new StoredObjectInfo(o.Id, StorageKeys.Create(o.Area, o.Id), o.Area, o.SizeBytes, o.Sha256 ?? [],
                o.DetectedMime ?? "application/octet-stream", o.ScanStatus);
    }

    public async Task<IReadOnlyDictionary<Guid, StoredObjectSummary>> GetAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
    {
        if (resourceIds.Count == 0) return new Dictionary<Guid, StoredObjectSummary>();
        var rows = await db.StorageObjects.AsNoTracking().Where(o => resourceIds.Contains(o.ResourceId)).ToListAsync(ct);
        return rows.ToDictionary(o => o.ResourceId, o => new StoredObjectSummary(
            o.ResourceId, o.DisplayName, o.DetectedMime, o.SizeBytes, o.Sha256 is null ? null : Convert.ToHexStringLower(o.Sha256),
            o.ScanStatus, o.ScannedAt is null ? null : DateTime.SpecifyKind(o.ScannedAt.Value, DateTimeKind.Utc)));
    }

    /// <summary>Queues byte deletion as a <c>delete-bytes</c> task (dedupe per object). Idempotent.</summary>
    public async Task QueueDeletionAsync(Guid resourceId, string reason, CancellationToken ct)
    {
        var id = await db.StorageObjects.AsNoTracking().Where(o => o.ResourceId == resourceId).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (id is null) return;
        var now = time.GetUtcNow().UtcDateTime;
        await tasks.EnqueueAsync(db, DeleteBytesTaskHandler.TaskKind, $"delete:{id}",
            JsonSerializer.Serialize(new { objectId = id, reason }), 6, now, now, ct);
    }

    /// <summary>Takedown path: objects to quarantine, kept (the legal hold itself is Resources' <c>legal_hold_until</c>).</summary>
    public async Task QuarantineAsync(Guid resourceId, DateTime legalHoldUntil, CancellationToken ct)
    {
        var o = await db.StorageObjects.AsNoTracking().FirstOrDefaultAsync(x => x.ResourceId == resourceId, ct);
        if (o is null || o.Area == StorageArea.Quarantine) return;
        await storage.MoveAsync(StorageKeys.Create(o.Area, o.Id), StorageArea.Quarantine, ct);
        await db.StorageObjects.Where(x => x.Id == o.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Area, StorageArea.Quarantine), ct);
    }

    public Task<int> CountOpenAsync(Guid userId, CancellationToken ct) =>
        db.UploadSessions.AsNoTracking().CountAsync(s => s.OwnerId == userId
            && (s.Status == UploadSessionStatus.Open || s.Status == UploadSessionStatus.Complete), ct);
}

/// <summary>C12: the content hash blocklist. Adding a hash here only records it; taking down every live resource with that
/// hash (addendum 13.4) and its evidence duties are slice S2 and PD-11 (OPEN).</summary>
public sealed class ContentHashBlocklist(FilesDbContext db, TimeProvider time) : IContentHashBlocklist
{
    public Task<bool> IsBlockedAsync(byte[] sha256, CancellationToken ct) =>
        db.BlockedHashes.AsNoTracking().AnyAsync(b => b.Sha256 == sha256, ct);

    public async Task<Result<int>> AddAsync(byte[] sha256, string reason, CancellationToken ct)
    {
        if (sha256.Length != 32) return Error.Validation("A SHA-256 hash is 32 bytes.");
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("A reason is required.");
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO files.blocked_content_hashes (sha256, reason, created_at) VALUES ({sha256}, {reason}, {time.GetUtcNow().UtcDateTime}) ON CONFLICT DO NOTHING", ct);
        return rows;
    }
}

/// <summary>The task <c>delete-bytes</c>: removes the object's file from whichever area holds it. Idempotent.</summary>
public sealed class DeleteBytesTaskHandler(FilesDbContext db, LocalObjectStorage storage) : ITaskHandler<FilesDbContext>
{
    public const string TaskKind = "delete-bytes";
    public string Kind => TaskKind;

    public async Task HandleAsync(ClaimedTask task, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(task.Payload);
        var id = doc.RootElement.GetProperty("objectId").GetGuid();
        foreach (var area in new[] { StorageArea.Temp, StorageArea.Objects, StorageArea.Quarantine })
            await storage.DeleteAsync(StorageKeys.Create(area, id), ct);
        await db.StorageObjects.Where(o => o.Id == id).ExecuteDeleteAsync(ct);
    }
}

/// <summary>ARC-009 section 12: idle sessions expire (temp bytes deleted, reservation released), finalized rows are cleaned up.</summary>
public sealed class UploadSessionExpiry(
    FilesDbContext db, TempUploadFiles temp, IContentQuota quota, TimeProvider time, IOptions<UploadOptions> options, ILogger<UploadSessionExpiry> logger)
{
    /// <returns>How many sessions expired.</returns>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expired = await db.UploadSessions
            .Where(s => (s.Status == UploadSessionStatus.Open || s.Status == UploadSessionStatus.Complete) && s.ExpiresAt < now)
            .Select(s => new { s.Id, s.ReservationId }).ToListAsync(ct);
        var count = 0;
        foreach (var session in expired)
        {
            // The conditional UPDATE is the claim: a PATCH racing the job sees status != Open and answers 410.
            var rows = await db.UploadSessions
                .Where(s => s.Id == session.Id && (s.Status == UploadSessionStatus.Open || s.Status == UploadSessionStatus.Complete) && s.ExpiresAt < now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, UploadSessionStatus.Expired), ct);
            if (rows == 0) continue;
            count++;
            try
            {
                await temp.DeleteAsync(session.Id, ct);
                if (session.ReservationId is { } reservationId) await quota.ReleaseAsync(reservationId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expired upload {UploadId}: cleanup failed; the sweep retries through the orphan job", session.Id);
            }
        }
        var cutoff = now.AddDays(-Math.Max(1, options.Value.FinalizedRetentionDays));
        await db.UploadSessions.Where(s => s.Status == UploadSessionStatus.Finalized && s.LastActivityAt < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }
}

/// <summary>Worker only: runs <see cref="UploadSessionExpiry"/> every 15 minutes.</summary>
public sealed class UploadSessionExpiryJob(IServiceScopeFactory scopes, ILogger<UploadSessionExpiryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var expired = await scope.ServiceProvider.GetRequiredService<UploadSessionExpiry>().ExpireAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Expired {Count} idle upload sessions.", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The upload expiry sweep failed; retrying at the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
