using Zaagl.Modules.Files.Contracts;

namespace Zaagl.Modules.Files.Domain;

/// <summary><c>files.upload_sessions</c> (ARC-009 section 3). The id is the tus file id and the storage object id.</summary>
public sealed class UploadSessionRow
{
    public Guid Id { get; set; }
    public Guid? OwnerId { get; set; }
    public byte[]? GuestCreatorKey { get; set; }
    public long DeclaredLength { get; set; }
    public long ReceivedLength { get; set; }
    /// <summary>Untrusted, display only; sanitised before it is ever shown.</summary>
    public string? DeclaredName { get; set; }
    /// <summary>Stored for support only; no decision reads it.</summary>
    public string? DeclaredType { get; set; }
    /// <summary>ResourceKind value of the declared kind (2 file, 3 media).</summary>
    public short IntendedKind { get; set; }
    public long MaxBytes { get; set; }
    public Guid? ReservationId { get; set; }
    /// <summary>Set by <c>/complete</c> step 4: the resume point.</summary>
    public Guid? ResourceId { get; set; }
    public UploadSessionStatus Status { get; set; }
    public short CreatedVia { get; set; }
    /// <summary>SHA-256 of the optional create <c>Idempotency-Key</c> (a retried create returns the same session).</summary>
    public byte[]? CreateKeyHash { get; set; }
    public byte[]? CompleteKeyHash { get; set; }
    public byte[]? CompleteBodyHash { get; set; }
    /// <summary>The 202 body of <c>/complete</c> without the link key (replay, ARC-002 Q1).</summary>
    public string? CompleteResponse { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary><c>files.storage_objects</c>: one object per upload, no cross-owner dedup in Phase 1.</summary>
public sealed class StorageObjectRow
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    public StorageArea Area { get; set; }
    public long SizeBytes { get; set; }
    public byte[]? Sha256 { get; set; }
    public string? DetectedMime { get; set; }
    public string? DetectedExt { get; set; }
    /// <summary>Sanitised (ARC-009 section 9.3); never part of a path.</summary>
    public string DisplayName { get; set; } = "";
    public ScanVerdict ScanStatus { get; set; }
    public string? ScanSignature { get; set; }
    public string? ScanEngine { get; set; }
    public DateTime? ScannedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BlockedHashRow
{
    public byte[] Sha256 { get; set; } = [];
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
