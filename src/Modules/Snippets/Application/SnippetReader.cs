using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Resources.Contracts;
using Zaagl.Modules.Snippets.Contracts;
using Zaagl.Modules.Snippets.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Snippets.Application;

/// <summary>
/// Read side (<see cref="ISnippetReader"/>) and the Snippets half of the resource lifecycle
/// (<see cref="IResourceKindHandler"/>). The caller has already run the gate; this class never decides access.
/// </summary>
public sealed class SnippetReader(SnippetsDbContext db, IResourceRegistry resources) : ISnippetReader, IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Snippet;

    public async Task<SnippetViewDto?> GetViewAsync(Guid resourceId, int? version, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var snippet = await db.Snippets.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, ct);
        if (snippet is null) return null;
        var wanted = version ?? snippet.CurrentVersion;
        var row = await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.ResourceId == resourceId && v.Version == wanted, ct);
        if (row is null) return null;
        var versions = await db.Versions.AsNoTracking().Where(v => v.ResourceId == resourceId)
            .OrderBy(v => v.Version).Select(v => v.Version).ToListAsync(ct);
        var resource = await resources.GetAsync(resourceId, ct);
        if (resource is null) return null;
        return new SnippetViewDto(resource.Title, row.Language, row.RenderMode, row.Version, versions, row.Content,
            row.SizeBytes, row.LineCount, DateTime.SpecifyKind(resource.ExpiresAt, DateTimeKind.Utc));
    }

    public async Task<Result<string>> GetRawAsync(Guid resourceId, int? version, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var wanted = version;
        if (wanted is null)
        {
            wanted = await db.Snippets.AsNoTracking().Where(s => s.ResourceId == resourceId)
                .Select(s => (int?)s.CurrentVersion).FirstOrDefaultAsync(ct);
            if (wanted is null) return Error.NotFound();
        }
        var content = await db.Versions.AsNoTracking().Where(v => v.ResourceId == resourceId && v.Version == wanted)
            .Select(v => v.Content).FirstOrDefaultAsync(ct);
        return content is null ? Error.NotFound() : Result<string>.Ok(content);
    }

    public async Task<IReadOnlyDictionary<Guid, SnippetSummaryDto>> GetSummariesAsync(
        IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0) return new Dictionary<Guid, SnippetSummaryDto>();
        var rows = await (from s in db.Snippets.AsNoTracking()
                          join v in db.Versions.AsNoTracking() on new { s.ResourceId, Version = s.CurrentVersion } equals new { v.ResourceId, v.Version }
                          where resourceIds.Contains(s.ResourceId)
                          select new { s.ResourceId, v.Version, v.Language, v.RenderMode, v.LineCount })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.ResourceId, r => new SnippetSummaryDto(r.Version, r.Language, r.RenderMode, r.LineCount));
    }

    public async Task<long> GetTotalBytesAsync(Guid resourceId, CancellationToken cancellationToken) =>
        await db.Versions.AsNoTracking().Where(v => v.ResourceId == resourceId).SumAsync(v => (long)v.SizeBytes, cancellationToken);

    /// <summary>Deletes the snippet rows (versions cascade). Idempotent.</summary>
    public async Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken) =>
        await db.Snippets.Where(s => s.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
}
