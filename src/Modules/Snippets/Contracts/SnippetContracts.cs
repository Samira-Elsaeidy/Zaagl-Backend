using System.Text.Json;
using System.Text.Json.Serialization;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Snippets.Contracts;

// Phase 1 snippet contracts (part of C2/C9). docs/phase1-implementation/contracts.md C9.1, addendum §§3, 11.
// Markdown is rendered by the client (PD-09 option A): the API is text only and has no renderedHtml field.
// Size caps per version: guest 256 KB, Free 512 KB, Starter 2 MB (PD-56, read from ContentLimits.MaxSnippetBytes).
// Custom aliases: allowed for every caller, guests included (PD-57).

/// <summary>Wire form: <c>code</c>, <c>plain</c>, <c>markdown</c>, <c>terminal</c> (openapi SnippetRenderMode).</summary>
[JsonConverter(typeof(SnippetRenderModeJsonConverter))]
public enum SnippetRenderMode : short
{
    Code = 0,
    Plain = 1,
    Markdown = 2,
    /// <summary>Terminal output from the CLI (ANSI kept or stripped at ingest).</summary>
    Terminal = 3,
}

/// <summary>Lower-case names on the wire; an unknown name or a number is a JSON error (400 validation).</summary>
public sealed class SnippetRenderModeJsonConverter()
    : JsonStringEnumConverter<SnippetRenderMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <param name="Content">UTF-8 text; the size cap is checked on the UTF-8 byte count.</param>
/// <param name="ExpiresAt">Requested expiry; clamped to the plan's retention.</param>
/// <param name="Public">PD-01: false (default) mints a link key; true creates a keyless link.</param>
public sealed record CreateSnippetCommand(
    string Content,
    string? Language,
    SnippetRenderMode RenderMode,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public = false);

/// <param name="ShortUrl">Without the fragment; clients append <c>#k=</c> + <paramref name="LinkKey"/> when present.</param>
public sealed record SnippetCreatedDto(
    Guid ResourceId,
    string Code,
    string ShortUrl,
    string? LinkKey,
    int Version,
    int SizeBytes,
    int LineCount,
    DateTime ExpiresAt,
    bool IsGuest);

/// <param name="Language">F-5 (contracts.md). Trailing optional, source-compatible.</param>
/// <param name="RenderMode">F-5.</param>
public sealed record SnippetVersionDto(
    int Version,
    int SizeBytes,
    int LineCount,
    DateTime CreatedAt,
    string? Language = null,
    SnippetRenderMode RenderMode = SnippetRenderMode.Code);

/// <summary>Viewer payload; the raw text is plain data, rendered inertly by the client.</summary>
public sealed record SnippetViewDto(
    string? Title,
    string? Language,
    SnippetRenderMode RenderMode,
    int Version,
    IReadOnlyList<int> Versions,
    string Content,
    int SizeBytes,
    int LineCount,
    DateTime ExpiresAt);

/// <summary>One side of a diff row. <see cref="Type"/> is <c>unchanged</c>, <c>inserted</c>, <c>deleted</c>,
/// <c>modified</c> or <c>imaginary</c> (padding opposite an insert or a delete; <see cref="No"/> null, empty text).</summary>
public sealed record SnippetDiffSide(int? No, string Text, string Type);

public sealed record SnippetDiffRow(SnippetDiffSide Left, SnippetDiffSide Right);

public sealed record SnippetDiffStats(int Added, int Removed, int Changed);

/// <summary>BE-009 (ARC-010 §10): text-only line diff between two versions of one snippet.</summary>
public sealed record SnippetDiffDto(int From, int To, IReadOnlyList<SnippetDiffRow> Lines, SnippetDiffStats Stats);

/// <summary>F-6: the snippet block of an owner list row.</summary>
public sealed record SnippetSummaryDto(int Version, string? Language, SnippetRenderMode RenderMode, int LineCount);

/// <summary>Read side for the viewer API and the raw route. Gates are evaluated by the caller (IAccessGate).</summary>
public interface ISnippetReader
{
    /// <summary>Null when the resource has no snippet rows or <paramref name="version"/> does not exist. Title and
    /// expiry come from the resource registry; <see cref="SnippetViewDto.Versions"/> lists every version number
    /// ascending (the public route narrows it to the current one).</summary>
    Task<SnippetViewDto?> GetViewAsync(Guid resourceId, int? version, CancellationToken cancellationToken);

    /// <summary>The text exactly as stored (<c>not_found</c> for an unknown resource or version).</summary>
    Task<Result<string>> GetRawAsync(Guid resourceId, int? version, CancellationToken cancellationToken);

    /// <summary>F-6: current-version summaries for owner list rows.</summary>
    Task<IReadOnlyDictionary<Guid, SnippetSummaryDto>> GetSummariesAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken);

    /// <summary>F-6: sum of the logical UTF-8 sizes of every stored version (quota reconciliation); 0 when unknown.</summary>
    Task<long> GetTotalBytesAsync(Guid resourceId, CancellationToken cancellationToken);
}
