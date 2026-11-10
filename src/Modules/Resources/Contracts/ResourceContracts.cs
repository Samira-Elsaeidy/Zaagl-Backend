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
    /// <summary>Stage 2 (BE-008): a create flow failed after the resource was registered (for example an alias
    /// conflict). The resource never gets a link, so it stays unreachable; the reservation is released.</summary>
    public const string CreateFailed = "create-failed";
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
/// <param name="AdoptReservationId">F-9 (ARC-009 §6 step 4): an upload reserves its declared bytes at tus create; when set,
/// <c>RegisterAsync</c> does not reserve again but verifies that reservation (state reserved, same owner,
/// <c>bytes = DeclaredSizeBytes</c>, not yet bound to a resource) and sets its <c>resource_id</c>. Null: unchanged behaviour.</param>
public sealed record RegisterResourceCommand(
    ResourceKind Kind,
    ContentOwner Owner,
    long DeclaredSizeBytes,
    DateTime ExpiresAt,
    string? Title,
    bool BurnAfterDownload,
    CreatedVia CreatedVia,
    Guid? AdoptReservationId = null);

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
    DateTime CreatedAt,
    ProcessingStatus Processing = ProcessingStatus.Pending);

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

    /// <summary>F-10: as above, with the evidence Files passes for <c>infected</c> and <c>blocked-hash</c> (hash and
    /// quarantine key). Resources applies T8 (bytes quarantined, legal hold, quota released) and forwards the evidence
    /// to the takedown (C12) when one is available. Kept as an overload so existing callers stay source-compatible.</summary>
    Task<Result<bool>> MarkRejectedAsync(Guid resourceId, string reason, RejectionEvidence? evidence, CancellationToken cancellationToken);

    Task<ResourceDescriptor?> GetAsync(Guid resourceId, CancellationToken cancellationToken);

    Task<ResourceDescriptor?> GetByLinkAsync(Guid linkId, CancellationToken cancellationToken);
}

/// <summary>F-10: evidence Files passes with an <c>infected</c> or <c>blocked-hash</c> rejection (Files cannot call Links).</summary>
public sealed record RejectionEvidence(byte[]? Sha256, string? ObjectKey);

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

    /// <summary>F-4 (contracts.md): reserve bytes for a new snippet version of an existing resource (the owner is read
    /// from the resource row). Guests have no ledger and get a synthetic reservation.</summary>
    Task<Result<QuotaReservation>> ReserveForResourceAsync(Guid resourceId, long bytes, CancellationToken cancellationToken);

    /// <summary>F-4: settles a reservation taken with <see cref="ReserveForResourceAsync"/>: reserved becomes used and the
    /// resource's usage row grows by <paramref name="actualBytes"/>. Idempotent per reservation.</summary>
    Task<Result<bool>> CommitToResourceAsync(Guid reservationId, long actualBytes, CancellationToken cancellationToken);
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
    /// <summary>503 <c>busy</c> with <c>Retry-After: 1</c>: the claim could not take its lock in 3 s; nothing was consumed (ARC-019 section 5).</summary>
    Busy,
}

/// <summary>What the hop must record once the object, size and Range checks have passed (ARC-019 section 5: a refused request
/// consumes nothing, so the record is a second step, <see cref="IDownloadGrants.ConsumeAsync"/>).</summary>
public enum DeliveryConsume : short
{
    /// <summary>Nothing to record (HEAD, or a purpose that <c>AuthorizeAsync</c> already counted).</summary>
    None = 0,
    /// <summary>First GET of a non-burn Download ticket: <c>first_used_at</c>, <c>request_count = 1</c>, <c>download_count + 1</c>.</summary>
    FirstUse = 1,
    /// <summary>A later GET of a used non-burn Download ticket (reuse window).</summary>
    Continue = 2,
    /// <summary>The burn claim: first GET of a Download ticket on a burn file.</summary>
    BurnClaim = 3,
    /// <summary>A later GET of the claiming grant inside its grace.</summary>
    BurnContinue = 4,
}

/// <param name="Derivative">The ticket's derivative (for snippets <c>v&lt;n&gt;</c>), echoed so the hop knows which
/// version to serve. Added in Stage 2 (source-compatible, trailing optional).</param>
/// <param name="Consume">S2: what <see cref="IDownloadGrants.ConsumeAsync"/> must still record; None unless an allowed Download.</param>
/// <param name="GrantId">The grant of the ticket (set together with <paramref name="Consume"/>).</param>
public sealed record DeliveryDecision(
    DeliveryOutcome Outcome, Guid? ResourceId, DeliveryPurpose? Purpose, DeliveryDisposition? Disposition, string? Derivative = null,
    DeliveryConsume Consume = DeliveryConsume.None, Guid? GrantId = null);

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

    /// <summary>
    /// S2 (ARC-019 section 5 step 7): called by the hop after the object, size and Range checks passed, for a decision whose
    /// <see cref="DeliveryDecision.Consume"/> is not None. A burn claim is the two statements of ARC-006 section 5 in one transaction
    /// with <c>lock_timeout 3s</c>: exactly one request wins; the same grant meeting itself is a continuation; any other loser is
    /// <see cref="DeliveryOutcome.Burned"/>. A lock timeout is <see cref="DeliveryOutcome.Busy"/> and consumes nothing.
    /// </summary>
    Task<DeliveryDecision> ConsumeAsync(DeliveryDecision decision, CancellationToken cancellationToken);
}

/// <summary>
/// Resources asks Files to act on bytes without referencing it (Files references Resources, not the other way round). Implemented
/// by Files; every method is idempotent. Used by the burn finaliser, the purge and guest-expiry sweeps, the orphan sweep and the
/// C10 handler (ARC-006 T11, T13, T16, T18, T19).
/// </summary>
public interface IResourceBytes
{
    /// <summary>Queues the <c>delete-bytes</c> task for the resource's object (no object: nothing to do).</summary>
    Task QueueDeletionAsync(Guid resourceId, string reason, CancellationToken cancellationToken);

    /// <summary>Moves the object to quarantine; the legal hold itself is the resource row's <c>legal_hold_until</c>.</summary>
    Task QuarantineAsync(Guid resourceId, DateTime legalHoldUntil, CancellationToken cancellationToken);
}
