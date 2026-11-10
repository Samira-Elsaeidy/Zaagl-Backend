using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Contracts;

namespace Zaagl.Modules.Snippets.Domain;

/// <summary><c>snippets.snippets</c> (ARC-010 §2). One row per snippet; the resource id is the Resources row's id (no
/// cross-schema foreign key, module rule). Title, owner, expiry, visibility and state live in Resources and Links.</summary>
public sealed class SnippetRow
{
    public Guid ResourceId { get; set; }
    public int CurrentVersion { get; set; }
    public int VersionCount { get; set; }
    /// <summary>Sum of <see cref="SnippetVersionRow.SizeBytes"/> of all versions (quota cross-check).</summary>
    public long TotalBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary><c>snippets.snippet_versions</c>: immutable (a database trigger refuses any change except
/// <see cref="QuotaCommitted"/> and <see cref="ReservationId"/>).</summary>
public sealed class SnippetVersionRow
{
    public Guid ResourceId { get; set; }
    public int Version { get; set; }
    /// <summary>Exactly the UTF-8 text received (ARC-010 §4.3).</summary>
    public string Content { get; set; } = "";
    public string? Language { get; set; }
    public SnippetRenderMode RenderMode { get; set; }
    public int SizeBytes { get; set; }
    public int LineCount { get; set; }
    public byte[] Sha256 { get; set; } = [];
    public Guid? ReservationId { get; set; }
    /// <summary>False between the version insert and <c>CommitToResourceAsync</c> (ARC-010 §5).</summary>
    public bool QuotaCommitted { get; set; } = true;
    public CreatedVia CreatedVia { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary><c>snippets.idempotency_keys</c>: create (and optional PUT) replays. The plain link key is never stored.</summary>
public sealed class SnippetIdempotencyRow
{
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public byte[] RequestHash { get; set; } = [];
    /// <summary>0 = pending, 1 = done.</summary>
    public short Status { get; set; }
    public Guid? ResourceId { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
}
