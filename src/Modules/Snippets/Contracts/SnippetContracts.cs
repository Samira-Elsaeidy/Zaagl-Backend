using Zaagl.SharedKernel;

namespace Zaagl.Modules.Snippets.Contracts;

// Phase 1 snippet contracts (part of C2/C9). docs/phase1-implementation/contracts.md, addendum §§3, 11.
// Types and interfaces only. Markdown rendering location is OPEN (PD-09). Size caps per version: guest 256 KB,
// Free 512 KB, Starter 2 MB (PD-56, read from ContentLimits.MaxSnippetBytes). Version limit and diff scope stay
// PROPOSED (PD-17). Custom aliases: allowed for every caller, guests included (PD-57).

public enum SnippetRenderMode : short
{
    Code = 0,
    Plain = 1,
    Markdown = 2,
    /// <summary>Terminal output from the CLI (ANSI kept or stripped at ingest).</summary>
    Terminal = 3,
}

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

public sealed record SnippetVersionDto(int Version, int SizeBytes, int LineCount, DateTime CreatedAt);

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

/// <summary>Read side for the viewer API and the raw route. Gates are evaluated by the caller (IAccessGate).</summary>
public interface ISnippetReader
{
    Task<SnippetViewDto?> GetViewAsync(Guid resourceId, int? version, CancellationToken cancellationToken);

    Task<Result<string>> GetRawAsync(Guid resourceId, int? version, CancellationToken cancellationToken);
}
