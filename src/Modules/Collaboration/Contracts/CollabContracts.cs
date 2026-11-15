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

// ---- Drop-off portals (E2.3, docs/phase2/drop-portals.md) ------------------------------------------------------------------

/// <param name="AllowedTypes"><c>image</c>, <c>video</c>, <c>audio</c>, <c>pdf</c>, <c>archive</c>, <c>model</c>, <c>other</c>; empty or null = any.</param>
public sealed record CreateDropPortalCommand(
    string? Title,
    string? Instructions,
    int? MaxFiles,
    long? MaxBytes,
    long? MaxFileBytes,
    IReadOnlyList<string>? AllowedTypes,
    bool RequireName,
    bool RequireEmail,
    bool NotifyOwner,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public);

/// <summary>PATCH: a null field is unchanged; <c>instructions: ""</c> clears them.</summary>
public sealed record UpdateDropPortalCommand(
    string? Instructions,
    int? MaxFiles,
    long? MaxBytes,
    long? MaxFileBytes,
    IReadOnlyList<string>? AllowedTypes,
    bool? RequireName,
    bool? RequireEmail,
    bool? NotifyOwner);

/// <param name="State">Link state: <c>ready</c> (live), <c>expired</c>, <c>deleted</c>, <c>disabled</c>, <c>taken-down</c>.</param>
/// <param name="Accepting">True when uploads are accepted now (live link, not closed, caps not reached).</param>
public sealed record OwnerDropPortalDto(
    Guid Id,
    string Code,
    string ShortUrl,
    string? Title,
    string State,
    bool Accepting,
    DateTime? ClosedAt,
    DateTime ExpiresAt,
    bool IsPublic,
    bool HasPassword,
    string? Instructions,
    int MaxFiles,
    long MaxBytes,
    long MaxFileBytes,
    IReadOnlyList<string> AllowedTypes,
    bool RequireName,
    bool RequireEmail,
    bool NotifyOwner,
    int FilesReceived,
    long BytesReceived,
    DateTime CreatedAt);

/// <summary>Public descriptor for an uploader. Holds nothing about other uploads (no counts, no names).</summary>
/// <param name="State"><c>open</c>, <c>closed</c> (by the owner) or <c>full</c> (a cap is reached).</param>
public sealed record DropDescriptorDto(
    string Kind,
    string State,
    string? Title,
    string? Instructions,
    DateTime ExpiresAt,
    bool HasPassword,
    long MaxFileBytes,
    IReadOnlyList<string> AllowedTypes,
    bool RequireName,
    bool RequireEmail);

/// <param name="Scan"><c>pending</c>, <c>clean</c>, <c>infected</c>, <c>error</c>: the owner can download once <c>clean</c> and
/// <c>State</c> is <c>ready</c> (POST /api/resources/{id}/download).</param>
public sealed record DropFileDto(
    Guid ResourceId,
    string? FileName,
    long SizeBytes,
    string? Mime,
    string Scan,
    string State,
    string? UploaderName,
    string? UploaderEmail,
    string? Note,
    DateTime ReceivedAt);

/// <summary>What an uploader learns after <c>/complete</c>: the file was received (never a link to it).</summary>
public sealed record DropReceiptDto(Guid UploadId, string Status, string? FileName, long SizeBytes);

// ---- Clipboard tables (E2.3, docs/phase2/clipboard-tables.md) --------------------------------------------------------------------

/// <param name="Format"><c>auto</c> (default: JSON when the text starts with <c>[</c> or <c>{</c>), <c>json</c> or <c>csv</c>.</param>
/// <param name="Delimiter">CSV only: <c>,</c> <c>;</c> <c>tab</c> or <c>|</c>; detected from the first line when absent.</param>
/// <param name="Header">CSV only: the first row holds the column names (default true).</param>
public sealed record CreateTableCommand(
    string? Text,
    string? Format,
    string? Delimiter,
    bool? Header,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public);

/// <param name="Type"><c>integer</c>, <c>number</c>, <c>boolean</c>, <c>date</c>, <c>datetime</c>, <c>string</c>, <c>json</c> (nested value kept as
/// JSON text) or <c>empty</c>.</param>
public sealed record TableColumnDto(string Name, string Type);

/// <summary>Public descriptor: one page of rows (<paramref name="Offset"/>, <paramref name="Count"/> of <paramref name="RowCount"/>). Every
/// cell is a JSON string, number, boolean or null: data, never markup or a formula.</summary>
public sealed record TableDescriptorDto(
    string Kind,
    string? Title,
    DateTime ExpiresAt,
    bool HasPassword,
    string SourceFormat,
    int RowCount,
    IReadOnlyList<TableColumnDto> Columns,
    int Offset,
    int Count,
    System.Text.Json.JsonElement Rows);

public sealed record OwnerTableDto(
    Guid Id,
    string Code,
    string ShortUrl,
    string? Title,
    string State,
    DateTime ExpiresAt,
    bool IsPublic,
    bool HasPassword,
    string SourceFormat,
    int RowCount,
    IReadOnlyList<TableColumnDto> Columns,
    int Offset,
    int Count,
    System.Text.Json.JsonElement Rows,
    int StoredBytes,
    DateTime CreatedAt);

/// <summary>A download as data: the browser saves <paramref name="Content"/> under <paramref name="FileName"/> (no bytes are served from
/// the API origin as a document).</summary>
public sealed record TableExportDto(string FileName, string Mime, string Content);

// ---- Voice-over micro-briefs (E2.3, docs/phase2/voice-briefs.md) ----------------------------------------------------------------

/// <param name="SubjectResourceId">One of the owner's ready images (PNG, JPEG, GIF, WebP) or PDFs.</param>
/// <param name="AudioResourceId">One of the owner's ready audio recordings (WebM/MP4 from MediaRecorder, or MP3, WAV, OGG, AAC, M4A, FLAC).</param>
/// <param name="DurationMs">The recording's length as measured by the recorder (1 .. plan maximum).</param>
/// <param name="Timeline">Optional <c>{"v":1,"events":[{"t","k","x","y","p","text"}]}</c>.</param>
public sealed record CreateBriefCommand(
    Guid SubjectResourceId,
    Guid AudioResourceId,
    int? DurationMs,
    System.Text.Json.JsonElement Timeline,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public);

/// <summary>One part of a brief. <paramref name="Kind"/>: <c>image</c>, <c>document</c> or <c>audio</c>; <paramref name="State"/>:
/// <c>ready</c> or <c>unavailable</c> (then every URL is null). URLs are short-lived inline/preview tickets on <c>usercontent.zaa.gl</c>.</summary>
public sealed record BriefMediaDto(
    string Kind,
    string State,
    string? Mime,
    long? SizeBytes,
    int? Width,
    int? Height,
    string? InlineUrl,
    string? PreviewUrl);

/// <param name="State"><c>ready</c> when the subject and the audio are both usable, otherwise <c>incomplete</c>.</param>
public sealed record BriefDescriptorDto(
    string Kind,
    string State,
    string? Title,
    DateTime ExpiresAt,
    bool HasPassword,
    int DurationMs,
    BriefMediaDto Subject,
    BriefMediaDto Audio,
    System.Text.Json.JsonElement Timeline);

public sealed record OwnerBriefDto(
    Guid Id,
    string Code,
    string ShortUrl,
    string? Title,
    string State,
    DateTime ExpiresAt,
    bool IsPublic,
    bool HasPassword,
    Guid SubjectResourceId,
    Guid AudioResourceId,
    int DurationMs,
    int EventCount,
    BriefMediaDto Subject,
    BriefMediaDto Audio,
    DateTime CreatedAt);

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
