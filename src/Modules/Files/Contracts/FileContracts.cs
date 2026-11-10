using Zaagl.SharedKernel;

namespace Zaagl.Modules.Files.Contracts;

// Phase 1 contracts C7 (object storage), C8 (upload protocol), C12 (hash blocklist).
// docs/phase1-implementation/contracts.md, addendum §§6-8, §13. Types and interfaces only.
// This project references only SharedKernel today, so resource ids are plain Guids.

// ---- C8: upload protocol (PD-12: tus 1.0 on api.zaa.gl through Cloudflare) ----

public static class UploadProtocol
{
    public const string TusVersion = "1.0.0";

    /// <summary>PD-12: a single request through Cloudflare must stay under its 100 MB body limit; chunks are
    /// at most 90,000,000 bytes. The server refuses larger PATCH bodies with 413 <c>chunk-too-large</c>.</summary>
    public const long MaxChunkBytes = 90_000_000;

    /// <summary>Recommended client chunk size (64 MiB).</summary>
    public const long DefaultChunkBytes = 64L * 1024 * 1024;

    /// <summary>Idle upload sessions expire after this many hours; temp bytes deleted, reservation released.</summary>
    public const int SessionIdleHours = 24;

    // Upload-Metadata keys (values base64, tus 1.0). All are untrusted display hints.
    public const string MetadataFileName = "filename";
    public const string MetadataFileType = "filetype";
    /// <summary><c>file</c> or <c>media</c>.</summary>
    public const string MetadataKind = "kind";
    /// <summary><c>1</c> or <c>true</c>: the uploader intends burn after downloading (refused at create without the capability, and for media).</summary>
    public const string MetadataBurn = "burn";
}

public enum UploadSessionStatus : short
{
    Open = 0,
    Complete = 1,
    Finalized = 2,
    Aborted = 3,
    Expired = 4,
}

/// <summary>Body of <c>POST /api/uploads/{id}/complete</c> (Idempotency-Key required).</summary>
public sealed record CompleteUploadRequest(
    string? Title,
    DateTime? ExpiresAt,
    bool BurnAfterDownload,
    string? Password,
    string? CustomSlug,
    bool Public);

/// <param name="ShortUrl">Without the fragment; clients append <c>#k=</c> + <paramref name="LinkKey"/> when present.</param>
public sealed record CompleteUploadResponse(
    Guid ResourceId,
    string Code,
    string ShortUrl,
    string? LinkKey,
    string State,
    DateTime ExpiresAt);

// ---- Scanning (addendum §7, fail-closed) ----

public enum ScanVerdict : short
{
    Pending = 0,
    Clean = 1,
    Infected = 2,
    /// <summary>Engine error or timeout: retried with backoff, then the resource is rejected (never served).</summary>
    Error = 3,
}

// ---- C7: object storage v2 (replaces the unused SharedKernel IFileStorage in BE-004) ----

public enum StorageArea : short
{
    Temp = 0,
    Objects = 1,
    Quarantine = 2,
    Previews = 3,
    Thumbnails = 4,
}

/// <param name="StorageKey">Generated, never from user input, e.g. <c>objects/3f/a2/&lt;uuid&gt;</c>, where the
/// shard is taken from a hash of the id (not its time-ordered prefix).</param>
public sealed record StoredObjectInfo(
    Guid ObjectId,
    string StorageKey,
    StorageArea Area,
    long SizeBytes,
    byte[] Sha256,
    string DetectedMime,
    ScanVerdict ScanVerdict);

public sealed record PutResult(string StorageKey, long SizeBytes, byte[] Sha256);

/// <summary>C7: storage operations. Keys are validated against a strict pattern (no traversal).</summary>
public interface IObjectStorage
{
    Task<PutResult> PutAsync(StorageArea area, Stream content, CancellationToken cancellationToken);

    /// <summary>Atomic rename on one filesystem (temp → objects, objects → quarantine).</summary>
    Task<string> MoveAsync(string storageKey, StorageArea target, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string storageKey, long offset, long? length, CancellationToken cancellationToken);

    Task<long?> StatAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Idempotent: a missing key is success.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Internal Nginx location for <c>X-Accel-Redirect</c>, e.g. <c>/_protected/objects/3f/a2/&lt;uuid&gt;</c>.</summary>
    string GetInternalPath(string storageKey);
}

/// <summary>Read side used by Media and by the delivery authorize hop.</summary>
public interface IStoredObjects
{
    Task<StoredObjectInfo?> GetForResourceAsync(Guid resourceId, CancellationToken cancellationToken);

    /// <summary>Queues byte deletion through the files.storage_deletions outbox. Idempotent.</summary>
    Task QueueDeletionAsync(Guid resourceId, string reason, CancellationToken cancellationToken);

    /// <summary>Takedown path: move bytes to quarantine with a legal hold (<c>legal_hold_until</c>) instead of
    /// deleting them. The retention period, evidence duties and whether held bytes stay in backups are PD-11
    /// (OPEN, counsel): the hold length is configuration (<c>Abuse:EvidenceRetentionDays</c>), not a decided value.</summary>
    Task QuarantineAsync(Guid resourceId, DateTime legalHoldUntil, CancellationToken cancellationToken);
}

// ---- C12: content hash blocklist ----

public sealed record BlockedContentHash(byte[] Sha256, string Reason, DateTime CreatedAt);

public interface IContentHashBlocklist
{
    Task<bool> IsBlockedAsync(byte[] sha256, CancellationToken cancellationToken);

    /// <summary>Admin only. Adding a hash also takes down every live resource with that hash (addendum §13.4).
    /// Mass takedown and its evidence duties fall under PD-11 (OPEN, counsel); the legal process around this call is
    /// not decided by this contract.</summary>
    Task<Result<int>> AddAsync(byte[] sha256, string reason, CancellationToken cancellationToken);
}

// ---- F-11 (contracts.md): upload statistics ----

/// <summary>F-11: sessions <c>Open</c> or <c>Complete</c> of one account, for <c>openUploads</c> of <c>GET /api/me/storage</c>.</summary>
public interface IUploadStats
{
    Task<int> CountOpenAsync(Guid userId, CancellationToken cancellationToken);
}

// ---- S1 additions (BUILD-ORDER-E1.2-E1.3.md): owner and viewer reads of the stored object ----

/// <summary>What the owner library and the viewer need to know about a stored object, without any storage key.</summary>
/// <param name="Sha256">Hex of the stored bytes; null until the scan task has hashed them.</param>
public sealed record StoredObjectSummary(
    Guid ResourceId,
    string DisplayName,
    string? DetectedMime,
    long SizeBytes,
    string? Sha256,
    ScanVerdict Scan,
    DateTime? ScannedAt);

/// <summary>Batch read for list pages; ids without an object are simply absent from the result.</summary>
public interface IStoredObjectSummaries
{
    Task<IReadOnlyDictionary<Guid, StoredObjectSummary>> GetAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken);
}
