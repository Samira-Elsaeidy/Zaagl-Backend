using Zaagl.Modules.Resources.Contracts;

namespace Zaagl.Modules.Resources.Domain;

/// <summary>Bytes of a resource (ARC-006 §1 <c>bytes_state</c>).</summary>
public enum BytesState : short
{
    None = 0,
    Stored = 1,
    /// <summary>Quarantined under a legal hold (takedown, infected).</summary>
    Quarantined = 2,
    Deleted = 3,
}

public enum ReservationState : short
{
    Reserved = 0,
    Committed = 1,
    Released = 2,
}

/// <summary><c>resources.resources</c> (ARC-006 §1). State is computed by <see cref="Contracts.ResourceStateCalculator"/>,
/// never stored; every change is a guarded conditional UPDATE.</summary>
public sealed class ResourceRow
{
    public Guid Id { get; set; }
    /// <summary>16 CSPRNG bytes, base64url (22). Used in tickets only, never in share URLs.</summary>
    public string PublicId { get; set; } = "";
    public ResourceKind Kind { get; set; }
    public Guid? OwnerId { get; set; }
    public byte[]? GuestCreatorKey { get; set; }
    /// <summary>Set once by BindLink; never changes.</summary>
    public Guid? LinkId { get; set; }
    public string? Title { get; set; }
    public long DeclaredSizeBytes { get; set; }
    public long? SizeBytes { get; set; }
    public ProcessingStatus ProcessingStatus { get; set; }
    public string? RejectedReason { get; set; }
    public bool BurnAfterDownload { get; set; }
    public DateTime? BurnClaimedAt { get; set; }
    public Guid? BurnGrantId { get; set; }
    /// <summary>S-1 (ARC-019): durable count of first uses of non-owner Download grants (the burn claim counts as one). Grant cleanup must not lose it.</summary>
    public long DownloadCount { get; set; }
    /// <summary>Owner delete mirrored from LinkDeleted (C10).</summary>
    public DateTime? DeletedAt { get; set; }
    /// <summary>Copies of the link's dates, kept current by the C10 handler; used by sweeps only, never for state.</summary>
    public DateTime LinkExpiresAt { get; set; }
    public DateTime LinkPurgeAfter { get; set; }
    public BytesState BytesState { get; set; }
    public DateTime? LegalHoldUntil { get; set; }
    public Guid? ReservationId { get; set; }
    public CreatedVia CreatedVia { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary><c>resources.quota_ledger</c>: one row per account owner. Guests never have one.</summary>
public sealed class QuotaLedgerRow
{
    public Guid OwnerId { get; set; }
    public long UsedBytes { get; set; }
    public long ReservedBytes { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class QuotaReservationRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? ResourceId { get; set; }
    public long Bytes { get; set; }
    public ReservationState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SettledAt { get; set; }
}

/// <summary><c>resources.quota_usage</c>: one row per resource whose bytes count as used.</summary>
public sealed class QuotaUsageRow
{
    public Guid ResourceId { get; set; }
    public Guid OwnerId { get; set; }
    public long Bytes { get; set; }
    public DateTime? ReleasedAt { get; set; }
}

/// <summary><c>resources.download_grants</c> (PD-05). Used by BE-018 (tickets, burn claim); created now so the schema
/// is complete.</summary>
public sealed class DownloadGrantRow
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    public DeliveryPurpose Purpose { get; set; }
    public DateTime MintedAt { get; set; }
    public DateTime FirstUseBy { get; set; }
    public DateTime? FirstUsedAt { get; set; }
    public int RequestCount { get; set; }
    public bool IsBurnClaim { get; set; }
}
