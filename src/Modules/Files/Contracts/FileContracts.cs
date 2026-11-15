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

/// <summary>
/// Phase 2 E2.3 (docs/phase2/drop-portals.md): the coarse type families a drop-off portal may restrict uploads to. Always decided from
/// the type detected in the bytes at <c>/complete</c> (never the declared type or the name). Office documents (docx, xlsx, pptx, odt)
/// are ZIP containers and fall under <see cref="Archive"/>; anything not recognised is <see cref="Other"/>.
/// </summary>
public static class UploadTypeCategories
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Pdf = "pdf";
    public const string Archive = "archive";
    public const string Model = "model";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Image, Video, Audio, Pdf, Archive, Model, Other];

    public static string Of(string? detectedMime) => detectedMime switch
    {
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" => Image,
        "application/pdf" => Pdf,
        "application/zip" or "application/gzip" or "application/x-7z-compressed" => Archive,
        { } m when m.StartsWith("video/", StringComparison.Ordinal) => Video,
        { } m when m.StartsWith("audio/", StringComparison.Ordinal) => Audio,
        { } m when m.StartsWith("model/", StringComparison.Ordinal) => Model,
        _ => Other,
    };

    /// <summary>An empty list allows every family (executables and encrypted archives are refused for drop uploads regardless).</summary>
    public static bool Allows(IReadOnlyCollection<string> allowed, string? detectedMime) =>
        allowed.Count == 0 || allowed.Contains(Of(detectedMime), StringComparer.Ordinal);
}

/// <summary>What a drop-off portal has received so far (Phase 2 E2.3), counted exactly as the create-time caps count it.</summary>
public sealed record DropUsage(int Files, long Bytes, int LastHour);

public interface IDropUploadUsage
{
    Task<DropUsage> GetAsync(Guid portalId, CancellationToken cancellationToken);
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
    DateTime? ScannedAt,
    string? Viewer = null);

/// <summary>Batch read for list pages; ids without an object are simply absent from the result.</summary>
public interface IStoredObjectSummaries
{
    Task<IReadOnlyDictionary<Guid, StoredObjectSummary>> GetAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken);
}

// ---- S3 (ARC-011): image metadata and the thumbnail derivative ----

/// <summary>A derivative of a stored original (own id, own storage area). <paramref name="Status"/> is
/// <c>pending</c>, <c>ready</c>, <c>failed</c> or <c>skipped</c>; no storage key is ever exposed.</summary>
public sealed record StoredDerivative(Guid Id, string Kind, string Status, int? Width, int? Height, string? Mime, long? SizeBytes);

/// <param name="Width">The displayed size (after EXIF orientation); null when the probe failed.</param>
/// <param name="Orientation">The original EXIF orientation value 1-8.</param>
/// <param name="Animated">GIF, APNG or animated WebP with more than one frame.</param>
public sealed record StoredMedia(
    Guid ResourceId, bool Animated, int? Width, int? Height, int? Orientation, int? FrameCount, bool? HasAlpha,
    IReadOnlyList<StoredDerivative> Derivatives, IReadOnlyList<PaletteColor>? Palette = null);

/// <summary>Phase 2 (docs/phase2/palette-and-comments.md): one dominant colour; <paramref name="Share"/> is the fraction (0..1) of the
/// sampled opaque pixels closest to it. Colours are ordered by share, largest first.</summary>
public sealed record PaletteColor(string Hex, int R, int G, int B, double Share);

/// <summary>Batch read of the probe result and the derivatives written by the <c>media-preview</c> task.</summary>
public interface IMediaMetadata
{
    /// <summary>Ids that were not probed yet are simply absent.</summary>
    Task<IReadOnlyDictionary<Guid, StoredMedia>> GetAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken);

    /// <summary>The ready derivative of <paramref name="kind"/>, or null (not made, failed, skipped, or not yet).</summary>
    Task<StoredDerivative?> GetReadyDerivativeAsync(Guid resourceId, string kind, CancellationToken cancellationToken);
}

// ---- Phase 2 E2.2: watermarked previews (docs/phase2/approval-gate.md) ----

/// <summary>
/// Requests a watermarked preview derivative of an image (the approval gate's draft). The original is never modified or served by this:
/// the worker renders a new, downscaled image with the text burned in and stores it as a derivative of the same resource; it is
/// served only by a Preview ticket naming <see cref="KindFor"/>.
/// </summary>
public interface IWatermarkDerivatives
{
    /// <summary>The derivative kind for a watermark text: <c>wm-</c> plus 16 hex characters of SHA-256(text).</summary>
    string KindFor(string text);

    /// <summary>Queues the render (idempotent per resource and text) and returns the derivative kind.</summary>
    Task<string> RequestAsync(Guid resourceId, string text, CancellationToken cancellationToken);
}
