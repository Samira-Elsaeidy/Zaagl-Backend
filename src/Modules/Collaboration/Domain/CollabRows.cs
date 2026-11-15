namespace Zaagl.Modules.Collaboration.Domain;

/// <summary><c>collab.comparisons</c> (docs/phase2/compare-slider.md): a before/after pair of the owner's image resources. No
/// cross-schema foreign keys; the parts are re-checked on every read.</summary>
public sealed class ComparisonRow
{
    public Guid ResourceId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid BeforeResourceId { get; set; }
    public Guid AfterResourceId { get; set; }
    public string? BeforeLabel { get; set; }
    public string? AfterLabel { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary><c>collab.comment_settings</c> (docs/phase2/palette-and-comments.md): whether pinned comments are on for one of the owner's
/// resources. <see cref="CommentCount"/> is the live count, changed only under this row's lock (the per-resource cap holds under
/// concurrency).</summary>
public sealed class CommentSettingsRow
{
    public Guid ResourceId { get; set; }
    public Guid OwnerId { get; set; }
    public bool Enabled { get; set; }
    public bool AllowAnonymous { get; set; }
    public int CommentCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary><c>collab.comments</c>: a comment pinned at a normalised point of an image (or of one side of a comparison). Plain text;
/// never rendered as markup. No IP or user agent is stored.</summary>
public sealed class CommentRow
{
    public long Id { get; set; }
    public Guid ResourceId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Comparisons only: 0 before, 1 after. Null for a single image.</summary>
    public short? Side { get; set; }
    public string Body { get; set; } = "";
    /// <summary>0 owner, 1 signed-in visitor, 2 anonymous visitor.</summary>
    public short AuthorKind { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string? AuthorName { get; set; }
    /// <summary>0 open, 1 resolved.</summary>
    public short Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary><c>collab.drop_portals</c> (docs/phase2/drop-portals.md): a "Reverse Pigeon" drop-off portal. People without an account
/// upload into it; the files become ordinary resources of the owner (quota, library), attributed through <see cref="DropFileRow"/>.
/// The caps are enforced by Files under a per-portal advisory lock; this row holds the owner's settings.</summary>
public sealed class DropPortalRow
{
    public Guid ResourceId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Instructions { get; set; }
    public int MaxFiles { get; set; }
    public long MaxBytes { get; set; }
    public long MaxFileBytes { get; set; }
    /// <summary>Comma-separated <c>UploadTypeCategories</c>; empty = any.</summary>
    public string AllowedTypes { get; set; } = "";
    public bool RequireName { get; set; }
    public bool RequireEmail { get; set; }
    public bool NotifyOwner { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary><c>collab.drop_files</c>: one received file (its own File/Media resource of the portal owner) and what the uploader
/// typed. Visible to the owner only; never to other uploaders. No IP or user agent is stored.</summary>
public sealed class DropFileRow
{
    public Guid ResourceId { get; set; }
    public Guid PortalId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid UploadSessionId { get; set; }
    public string? UploaderName { get; set; }
    public string? UploaderEmail { get; set; }
    public string? Note { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary><c>collab.approvals</c> (docs/phase2/approval-gate.md): a draft image served watermarked until the link holder approves (or,
/// later, pays); then the clean original is released. <see cref="State"/> follows <see cref="Application.ApprovalStateMachine"/>.</summary>
public sealed class ApprovalRow
{
    public Guid ResourceId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DraftResourceId { get; set; }
    public string WatermarkText { get; set; } = "";
    /// <summary>The Files derivative kind of the watermarked preview (<c>wm-…</c>).</summary>
    public string WatermarkKind { get; set; } = "";
    public bool ReleaseOnApproval { get; set; }
    /// <summary>0 draft, 1 approved, 2 paid, 3 released.</summary>
    public short State { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApproverName { get; set; }
    public string? ApproverEmail { get; set; }
    public string? ApprovalNote { get; set; }
    public Guid? ApproverUserId { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaymentProvider { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
