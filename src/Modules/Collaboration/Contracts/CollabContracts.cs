namespace Zaagl.Modules.Collaboration.Contracts;

// Phase 2 E2.2 (docs/phase2/compare-slider.md, palette-and-comments.md, approval-gate.md). Wire DTOs of the Collaboration module.

public sealed record CreateComparisonCommand(
    Guid BeforeResourceId,
    Guid AfterResourceId,
    string? BeforeLabel,
    string? AfterLabel,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public);

/// <param name="LinkKey">The <c>#k=</c> value, returned once; null for a public comparison.</param>
public sealed record ToolLinkCreatedDto(Guid ResourceId, string Code, string ShortUrl, string? LinkKey, DateTime ExpiresAt);

/// <summary>One image of a tool link (comparison side, approval draft). <paramref name="State"/>: <c>ready</c> or
/// <c>unavailable</c> (then every URL is null). URLs are short-lived inline/preview tickets on <c>usercontent.zaa.gl</c>.</summary>
public sealed record ImagePartDto(
    string? Label,
    string State,
    string? Mime,
    int? Width,
    int? Height,
    string? InlineUrl,
    string? PreviewUrl);

/// <param name="State"><c>ready</c> when both sides are usable, otherwise <c>incomplete</c>.</param>
public sealed record CompareDescriptorDto(
    string Kind,
    string State,
    string? Title,
    DateTime ExpiresAt,
    bool HasPassword,
    ImagePartDto Before,
    ImagePartDto After);

public sealed record OwnerComparisonDto(
    Guid Id,
    string Code,
    string ShortUrl,
    string? Title,
    string State,
    DateTime ExpiresAt,
    bool IsPublic,
    bool HasPassword,
    Guid BeforeResourceId,
    Guid AfterResourceId,
    ImagePartDto Before,
    ImagePartDto After,
    DateTime CreatedAt);

// ---- Pinned comments ------------------------------------------------------------------------------------------------

/// <param name="X">Normalised horizontal position 0..1 from the left edge of the displayed (EXIF-oriented) image.</param>
/// <param name="Y">Normalised vertical position 0..1 from the top edge.</param>
/// <param name="Side">Comparisons only: <c>before</c> or <c>after</c>.</param>
/// <param name="AuthorName">Optional display name for a visitor (at most 60 characters); ignored for the owner.</param>
public sealed record PostCommentCommand(double X, double Y, string? Side, string? Body, string? AuthorName);

/// <param name="Author"><c>owner</c>, <c>visitor</c> (signed in) or <c>anonymous</c>.</param>
/// <param name="Mine">True when the signed-in caller wrote it (the UI may offer delete).</param>
public sealed record CommentDto(
    long Id,
    double X,
    double Y,
    string? Side,
    string Body,
    string Author,
    string? AuthorName,
    string Status,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    bool Mine);

/// <param name="Enabled">False: the owner has not turned comments on (the list is empty and posting is refused).</param>
public sealed record CommentThreadDto(bool Enabled, bool AllowAnonymous, int Count, int MaxComments, IReadOnlyList<CommentDto> Items);

public sealed record CommentSettingsDto(bool Enabled, bool AllowAnonymous, int Count, int MaxComments);

// ---- Watermark and approval gate ------------------------------------------------------------------------------------------

/// <param name="WatermarkText">Burned into the preview (1-60 characters); default "DRAFT".</param>
/// <param name="ReleaseOnApproval">True (default): approving releases the original at once. False: the owner releases it.</param>
public sealed record CreateApprovalCommand(
    Guid DraftResourceId,
    string? WatermarkText,
    bool ReleaseOnApproval,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public);

/// <param name="Name">Optional, at most 100 characters.</param>
/// <param name="Email">Optional, at most 254 characters, a plain address.</param>
/// <param name="Note">Optional message to the owner, at most 1,000 characters.</param>
public sealed record ApproveCommand(string? Name, string? Email, string? Note);

/// <summary>The approval record shown to the link holder and the owner. <paramref name="State"/>: <c>draft</c>, <c>approved</c>,
/// <c>paid</c> or <c>released</c>.</summary>
public sealed record ApprovalInfoDto(string State, DateTime? ApprovedAt, string? ApproverName, DateTime? PaidAt, DateTime? ReleasedAt);

/// <summary>Public descriptor. Before release <paramref name="Preview"/> is the watermarked derivative only (its <c>inlineUrl</c>
/// is null while it is still rendering: <paramref name="PreviewState"/> = <c>processing</c>) and <paramref name="Original"/> is
/// null. After release <paramref name="Original"/> carries an inline ticket and <c>POST .../download</c> works.</summary>
public sealed record ApprovalDescriptorDto(
    string Kind,
    string? Title,
    DateTime ExpiresAt,
    bool HasPassword,
    ApprovalInfoDto Approval,
    string PreviewState,
    ImagePartDto? Preview,
    ImagePartDto? Original,
    bool CanApprove);

public sealed record OwnerApprovalDto(
    Guid Id,
    string Code,
    string ShortUrl,
    string? Title,
    string State,
    DateTime ExpiresAt,
    bool IsPublic,
    bool HasPassword,
    Guid DraftResourceId,
    string WatermarkText,
    bool ReleaseOnApproval,
    ApprovalInfoDto Approval,
    string? ApproverEmail,
    string? ApprovalNote,
    string PreviewState,
    DateTime CreatedAt);
