using Zaagl.SharedKernel;

namespace Zaagl.Modules.Resources.Contracts;

// Phase 1 contract C2 (+ delivery grants, PD-05) — docs/phase1-implementation/contracts.md, addendum §§2, 7, 8, 9.
// Types and interfaces only. This project references only SharedKernel, so link ids are plain Guids and the
// kind numbers mirror Links.Contracts.LinkTargetKind (Snippet = 1, File = 2, Media = 3).

public enum ResourceKind : short
{
    Snippet = 1,
    File = 2,
    Media = 3,
}

/// <summary>Stored processing status (<c>resources.resources.processing_status</c>).</summary>
public enum ProcessingStatus : short
{
    Pending = 0,
    Scanning = 1,
    Ready = 2,
    Rejected = 3,
    /// <summary>Scan or processing failed; retried, then Rejected with <c>scan-unavailable</c> (fail-closed).</summary>
    Error = 4,
}

public static class RejectedReasons
{
    public const string Infected = "infected";
    public const string TypeNotAllowed = "type-not-allowed";
    public const string TooLarge = "too-large";
    public const string BlockedHash = "blocked-hash";
    public const string ScanUnavailable = "scan-unavailable";
}

/// <summary>
/// Computed state, never stored. Evaluated top to bottom, first match wins (mirrors spec §4.3 and addendum §2.4).
/// Only <see cref="Ready"/> may ever be served.
/// </summary>
public enum ResourceState
{
    Purgeable,
    Deleted,
    Disabled,
    Burned,
    Expired,
    Processing,
    Rejected,
    Ready,
}

/// <summary>Exactly one of the two is set (spec §7.12 guest identity).</summary>
public sealed record ContentOwner(Guid? UserId, byte[]? GuestCreatorKey)
{
    public bool IsGuest => UserId is null;
}

public enum CreatedVia : short
{
    Web = 0,
    Cli = 1,
    Desktop = 2,
    Api = 3,
}

/// <param name="DeclaredSizeBytes">Upload-Length or snippet byte size; reserved against the quota.</param>
/// <param name="ExpiresAt">Already clamped to the plan's retention.</param>
public sealed record RegisterResourceCommand(
    ResourceKind Kind,
    ContentOwner Owner,
    long DeclaredSizeBytes,
    DateTime ExpiresAt,
    string? Title,
    bool BurnAfterDownload,
    CreatedVia CreatedVia);

/// <param name="PublicId">Opaque 128-bit id (base64url, 22 chars), never derived from the code. Used in
/// delivery tickets, never in user-visible share URLs.</param>
public sealed record ResourceRegistration(Guid ResourceId, string PublicId);

public sealed record ResourceDescriptor(
    Guid ResourceId,
    string PublicId,
    ResourceKind Kind,
    ContentOwner Owner,
    Guid? LinkId,
    string? Title,
    long SizeBytes,
    ResourceState State,
    string? RejectedReason,
    DateTime ExpiresAt,
    DateTime? PurgeAfter,
    bool BurnAfterDownload,
    DateTime? BurnClaimedAt,
    long DownloadCount,
    DateTime CreatedAt);

/// <summary>
/// C2: the envelope every content kind shares. Order for every share (ARCHITECTURE.md §3):
/// Register (pending, quota reserved) → ILinkIssuer.CreateContentLinkAsync (idempotency key = resource id) → BindLink.
/// A sweeper removes pending resources with no link after 24 hours.
/// </summary>
public interface IResourceRegistry
{
    Task<Result<ResourceRegistration>> RegisterAsync(RegisterResourceCommand command, CancellationToken cancellationToken);

    Task<Result<bool>> BindLinkAsync(Guid resourceId, Guid linkId, CancellationToken cancellationToken);

    Task<Result<bool>> MarkScanningAsync(Guid resourceId, CancellationToken cancellationToken);

    /// <summary>Converts the quota reservation into used bytes with the final size.</summary>
    Task<Result<bool>> MarkReadyAsync(Guid resourceId, long finalSizeBytes, CancellationToken cancellationToken);

    /// <param name="reason">One of <see cref="RejectedReasons"/>.</param>
    Task<Result<bool>> MarkRejectedAsync(Guid resourceId, string reason, CancellationToken cancellationToken);

    Task<ResourceDescriptor?> GetAsync(Guid resourceId, CancellationToken cancellationToken);

    Task<ResourceDescriptor?> GetByLinkAsync(Guid linkId, CancellationToken cancellationToken);
}

public sealed record QuotaReservation(Guid ReservationId, ContentOwner Owner, long Bytes);

public sealed record StorageUsage(long UsedBytes, long ReservedBytes, long? QuotaBytes);

/// <summary>
/// C2: durable per-owner ledger. Every change runs in one transaction under the owner's advisory lock, so
/// used + reserved never exceeds the quota and never goes negative under concurrency.
/// </summary>
public interface IContentQuota
{
    /// <summary>Fails with <c>quota-exceeded</c> when used + reserved + bytes &gt; quota.</summary>
    Task<Result<QuotaReservation>> ReserveAsync(ContentOwner owner, long bytes, CancellationToken cancellationToken);

    Task<Result<bool>> CommitAsync(Guid reservationId, long actualBytes, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>Called when bytes are deleted (purge, guest expiry, burn). Owner delete keeps the bytes and the
    /// used space until purge_after (PD-55). Takedown timing is still open (ARC-006 Q2). Idempotent per resource.</summary>
    Task ReleaseUsedAsync(Guid resourceId, CancellationToken cancellationToken);

    Task<StorageUsage> GetUsageAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Implemented by Snippets, Files and Media; Resources calls it when a resource's data must go.</summary>
public interface IResourceKindHandler
{
    ResourceKind Kind { get; }

    /// <summary>Delete the kind's rows and queue byte deletion. Idempotent.</summary>
    Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken);
}

// ---- Delivery tickets and grants (PD-05, addendum §§7-8) ----

public enum DeliveryPurpose : short
{
    /// <summary>Attachment download. Minted only by a POST from the download button. Consumes burn on first GET.</summary>
    Download = 1,
    /// <summary>Inline original for allowlisted raster/video. Never consumes burn; never minted for burn files.</summary>
    Inline = 2,
    /// <summary>Derived preview or thumbnail. Never consumes burn; burn files have none.</summary>
    Preview = 3,
    /// <summary>Raw snippet text, <c>text/plain; charset=utf-8</c>.</summary>
    Raw = 4,
    /// <summary>Owner download or preview. Never consumes burn.</summary>
    Owner = 5,
}

public enum DeliveryDisposition : short
{
    Attachment = 1,
    Inline = 2,
}

/// <param name="Derivative">Media derivative kind for <see cref="DeliveryPurpose.Preview"/>, else null.</param>
public sealed record MintTicketRequest(Guid ResourceId, DeliveryPurpose Purpose, string? Derivative);

/// <param name="Url"><c>https://usercontent.zaa.gl/d/&lt;ticket&gt;</c> (host from configuration).</param>
public sealed record DeliveryTicket(string Url, DateTime ExpiresAt);

/// <summary>Claims of a ticket: HMAC-SHA256 with <c>kid</c> from a rotating key set (Content:TicketKeys).</summary>
public sealed record DeliveryTicketClaims(
    string KeyId,
    Guid GrantId,
    Guid ResourceId,
    DeliveryPurpose Purpose,
    string? Derivative,
    DateTime ExpiresAt);

public enum DeliveryOutcome
{
    Allowed,
    /// <summary>403 <c>ticket-invalid</c>: bad signature, unknown kid, expired, or grant exhausted.</summary>
    TicketInvalid,
    /// <summary>410 burned.</summary>
    Burned,
    /// <summary>410 expired, deleted, disabled or taken down.</summary>
    Gone,
    /// <summary>409: not yet Ready (fail-closed, no bytes).</summary>
    Processing,
    /// <summary>422 <c>content-rejected</c>.</summary>
    Rejected,
}

public sealed record DeliveryDecision(DeliveryOutcome Outcome, Guid? ResourceId, DeliveryPurpose? Purpose, DeliveryDisposition? Disposition);

/// <summary>
/// Download grants live in the resources schema with the burn claim, so the claim and the grant are one
/// transaction. The authorize hop on the user-content host (owned by Files) calls <see cref="AuthorizeAsync"/>.
/// </summary>
public interface IDownloadGrants
{
    /// <summary>Caller has already passed IAccessGate for the matching purpose. Fails for non-Ready resources
    /// and for Inline/Preview on burn files.</summary>
    Task<Result<DeliveryTicket>> MintAsync(MintTicketRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Validates the ticket and its grant. For a burn file and purpose Download, the first GET claims the burn
    /// atomically (exactly one winner); the same grant then serves up to 16 requests within 30 minutes
    /// (Burn:GraceMinutes, Burn:MaxRequests). HEAD never claims and never counts.
    /// </summary>
    Task<DeliveryDecision> AuthorizeAsync(string ticket, bool isHead, CancellationToken cancellationToken);
}
