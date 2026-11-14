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
