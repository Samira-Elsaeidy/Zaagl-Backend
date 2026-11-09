using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Contracts;

// Phase 1 contract C1 / C10 / C12 (docs/phase1-implementation/contracts.md, ADR-0001..0003).
// Types and interfaces only: the backend agent implements them in BE-007 / BE-019 / BE-022.

/// <summary>
/// What a link points at (addendum §2). Stored as <c>links.target_kind smallint not null default 0</c>.
/// The numbers of the content kinds match <c>ResourceKind</c> in Resources.Contracts.
/// Check constraint: <c>(target_kind = 0) = (resource_id IS NULL)</c>.
/// </summary>
public enum LinkTargetKind : short
{
    /// <summary>Phase 0 behaviour: a 302 to <c>destination_url</c>. Unchanged.</summary>
    Url = 0,
    /// <summary>Viewer <c>app.zaa.gl/p/&lt;code&gt;</c>.</summary>
    Snippet = 1,
    /// <summary>Viewer <c>app.zaa.gl/f/&lt;code&gt;</c>.</summary>
    File = 2,
    /// <summary>Viewer <c>app.zaa.gl/i/&lt;code&gt;</c>.</summary>
    Media = 3,
}

/// <summary>PD-02: first path segment of the viewer on <c>app.zaa.gl</c> per content kind.</summary>
public static class ViewerPathSegments
{
    public const string Snippet = "p";
    public const string Media = "i";
    public const string File = "f";
}

/// <summary>
/// Input of <see cref="ILinkIssuer.CreateContentLinkAsync"/>. Exactly one of <paramref name="OwnerId"/> and
/// <paramref name="GuestCreatorKey"/> is set (spec §4.3 check, §7.12).
/// </summary>
/// <param name="ResourceId">The Resources row this link points at; also the idempotency key of the call.</param>
/// <param name="Kind">Never <see cref="LinkTargetKind.Url"/>.</param>
/// <param name="ExpiresAt">Already clamped by the caller to <c>min(requested, now + plan retention)</c>
/// (addendum §9). Mandatory: every content link expires.</param>
/// <param name="CustomSlug">Optional alias; the Phase 0 alias syntax and reservation rules (spec §4.5) apply
/// unchanged. PD-57: allowed for every caller of a content link, guests included, regardless of the URL-link plan
/// flag <c>PlanLimits.CustomSlugs</c>; guest aliases follow the spec §7.12 guest rules and are released at expiry.</param>
/// <param name="Password">Plain text, hashed by the issuer; null for none.</param>
/// <param name="RequireLinkKey">PD-01: when true the issuer mints a 128-bit link key, stores only its hash,
/// and returns the plain key once in <see cref="ContentLinkIssued.LinkKey"/>.</param>
public sealed record ContentLinkRequest(
    Guid ResourceId,
    LinkTargetKind Kind,
    Guid? OwnerId,
    byte[]? GuestCreatorKey,
    DateTime ExpiresAt,
    string? CustomSlug = null,
    string? Title = null,
    string? Password = null,
    bool RequireLinkKey = true);

/// <param name="ShortUrl"><c>https://zaa.gl/&lt;code&gt;</c>, built from configuration (never from the request
/// Host header, spec MUST NOT #22) and without the fragment.</param>
/// <param name="LinkKey">Base64url, 22 characters, returned exactly once; null for a public link. Clients
/// append <c>#k=&lt;LinkKey&gt;</c> to <paramref name="ShortUrl"/>.</param>
public sealed record ContentLinkIssued(
    Guid LinkId,
    string Code,
    string ShortUrl,
    DateTime ExpiresAt,
    DateTime PurgeAfter,
    string? LinkKey);

/// <summary>
/// C1: content modules create links only through this contract. The implementation reuses the Phase 0
/// creation path (leases, alias lock, blocked aliases, denylist, restore freeze, idempotency) and skips the
/// URL-only steps (normalisation, destination blocklist, Web Risk, routing rules, UTM).
/// </summary>
public interface ILinkIssuer
{
    Task<Result<ContentLinkIssued>> CreateContentLinkAsync(ContentLinkRequest request, CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="IContentLinkCommands.RenewAsync"/>.</summary>
public sealed record ContentLinkRenewed(Guid LinkId, DateTime ExpiresAt, DateTime PurgeAfter);

/// <summary>
/// C1: owner operations on an existing content link, called by Resources for <c>PATCH/DELETE /api/resources/{id}</c>
/// after it has checked ownership (PD-40) and the plan (C5). Each operation runs in one Links transaction, writes the
/// matching C10 outbox row in that transaction, and invalidates the link cache. None of them applies to
/// <see cref="LinkTargetKind.Url"/> links (they fail with <c>not_found</c>). Design: design/ARC-002-link-target-model.md.
/// </summary>
public interface IContentLinkCommands
{
    /// <summary>Make private, or rotate: mints a new 128-bit key, replaces the stored hash, returns the plain key
    /// once (base64url, 22 chars). The previous key (or the keyless URL) stops working.</summary>
    Task<Result<string>> IssueNewLinkKeyAsync(Guid linkId, CancellationToken cancellationToken);

    /// <summary>Make public: removes the key hash (PD-01, PD-37, PD-45 owner choice).</summary>
    Task<Result<bool>> RemoveLinkKeyAsync(Guid linkId, CancellationToken cancellationToken);

    /// <summary>Sets (non-empty) or clears (null) the password. Outstanding access grants stop validating because
    /// their password stamp no longer matches (design/ARC-004-access-gate.md).</summary>
    Task<Result<bool>> SetPasswordAsync(Guid linkId, string? password, CancellationToken cancellationToken);

    /// <summary>Renewal (spec §7.4 rules: only forward). <paramref name="newExpiresAt"/> is already clamped by the
    /// caller to the plan maximum from now (addendum §9). Emits <see cref="LinkRenewed"/>.</summary>
    Task<Result<ContentLinkRenewed>> RenewAsync(Guid linkId, DateTime newExpiresAt, CancellationToken cancellationToken);

    /// <summary>Owner delete (spec §4.3 "deleted", 410). Emits <see cref="LinkDeleted"/>.</summary>
    Task<Result<bool>> DeleteAsync(Guid linkId, CancellationToken cancellationToken);
}

// ---- C10: link lifecycle events (Links-owned outbox links.lifecycle_outbox, drained by the worker) ----

/// <summary>
/// Emitted only for links whose <see cref="LinkTargetKind"/> is not <see cref="LinkTargetKind.Url"/>.
/// Written in the same transaction as the state change; delivered at least once, so handlers are idempotent.
/// </summary>
public abstract record LinkLifecycleEvent(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt);

/// <summary>Owner or admin deleted the link, or account deletion did (spec §7.14).</summary>
public sealed record LinkDeleted(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt)
    : LinkLifecycleEvent(OutboxId, LinkId, ResourceId, Kind, OccurredAt);

/// <param name="DisabledKind">One of <c>admin</c>, <c>user-disabled</c>, <c>abuse</c>, <c>safety</c> (spec §4.3).</param>
public sealed record LinkDisabled(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt, string DisabledKind)
    : LinkLifecycleEvent(OutboxId, LinkId, ResourceId, Kind, OccurredAt);

/// <summary>Disable cleared (user re-enable or audited reinstate).</summary>
public sealed record LinkEnabled(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt)
    : LinkLifecycleEvent(OutboxId, LinkId, ResourceId, Kind, OccurredAt);

/// <summary>Renewal moved expiry (and purge) forward; bytes are kept until the new purge_after (PD-06).</summary>
public sealed record LinkRenewed(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt, DateTime ExpiresAt, DateTime PurgeAfter)
    : LinkLifecycleEvent(OutboxId, LinkId, ResourceId, Kind, OccurredAt);

/// <summary>The link reached expires_at. Account content: enters quarantine, bytes kept (PD-06). Guest content:
/// purge_after = expires_at, bytes deleted now.</summary>
public sealed record LinkExpired(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt, bool IsGuest)
    : LinkLifecycleEvent(OutboxId, LinkId, ResourceId, Kind, OccurredAt);

/// <summary>The link row was purged; the resource and any remaining bytes must go (idempotent).</summary>
public sealed record LinkPurged(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt)
    : LinkLifecycleEvent(OutboxId, LinkId, ResourceId, Kind, OccurredAt);

/// <summary>Implemented by Resources; called by the worker's outbox drain. Must be idempotent.</summary>
public interface ILinkLifecycleHandler
{
    Task HandleAsync(LinkLifecycleEvent lifecycleEvent, CancellationToken cancellationToken);
}

// ---- C12: takedown requested by a content module (scan verdict, hash blocklist) ----

public enum ContentTakedownKind
{
    /// <summary><c>disabled_kind = 'abuse'</c>: manual or hash-blocklist match.</summary>
    Abuse,
    /// <summary><c>disabled_kind = 'safety'</c>: malware verdict.</summary>
    Safety,
}

/// <param name="Sha256">Of the original bytes, for the evidence row; null for snippets.</param>
public sealed record ContentTakedownRequest(Guid LinkId, Guid ResourceId, ContentTakedownKind Kind, string Reason, byte[]? Sha256);

/// <summary>
/// Runs the Phase 0 takedown routine (spec §7.15) for a content link: disable, block the alias, write evidence
/// with the content fields, notify the cache. Evidence retention, the legal hold length and the EU notice-and-action
/// duties behind this call are PD-11 (OPEN, counsel); this contract does not decide them.
/// </summary>
public interface ILinkTakedown
{
    Task<Result<bool>> TakeDownContentAsync(ContentTakedownRequest request, CancellationToken cancellationToken);
}

// ---- C1 addition (Stage 2, contracts.md change F-4, for architect review) ----

/// <summary>
/// The cached link snapshot by link id. Content modules only know the link id of their resource; the owner routes
/// and the user-content authorize hop need the authoritative link state (ARC-006 §2), and the hop must see a
/// takedown within the spec §7.3 invalidation bound, so it reads the same NOTIFY-invalidated snapshot as the
/// redirect. Returns null for an unknown id.
/// </summary>
public interface ILinkSnapshots
{
    Task<LinkSnapshot?> GetSnapshotByIdAsync(Guid linkId, CancellationToken cancellationToken);
}
