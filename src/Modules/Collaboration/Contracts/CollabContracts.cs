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
