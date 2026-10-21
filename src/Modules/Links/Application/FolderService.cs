using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

public sealed class FolderService(LinksDbContext db)
{
    private const int MaxFolders = 100;

    public async Task<IReadOnlyList<FolderDto>> ListAsync(Guid userId, CancellationToken ct) =>
        await db.Folders.AsNoTracking().Where(f => f.OwnerId == userId)
            .OrderBy(f => f.Name)
            .Select(f => new FolderDto(f.Id, f.Name, db.Links.Count(l => l.FolderId == f.Id)))
            .ToListAsync(ct);

    public async Task<Result<FolderDto>> CreateAsync(Guid userId, string? name, CancellationToken ct)
    {
        var clean = Clean(name);
        if (clean is null) return Error.Validation("Folder name must be 1-100 characters.");
        if (await db.Folders.CountAsync(f => f.OwnerId == userId, ct) >= MaxFolders)
            return Error.Conflict($"You can have at most {MaxFolders} folders.");

        var folder = new Folder { OwnerId = userId, Name = clean };
        db.Folders.Add(folder);
        return await SaveAsync(folder, 0, ct);
    }

    public async Task<Result<FolderDto>> RenameAsync(Guid userId, Guid id, string? name, CancellationToken ct)
    {
        var clean = Clean(name);
        if (clean is null) return Error.Validation("Folder name must be 1-100 characters.");

        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId, ct);
        if (folder is null) return Error.NotFound("Folder not found.");

        folder.Name = clean;
        var count = await db.Links.CountAsync(l => l.FolderId == id, ct);
        return await SaveAsync(folder, count, ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        if (!await db.Folders.AnyAsync(f => f.Id == id && f.OwnerId == userId, ct)) return false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Links.Where(l => l.FolderId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.FolderId, (Guid?)null), ct);
        await db.Folders.Where(f => f.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<Result<FolderDto>> SaveAsync(Folder folder, int linkCount, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("A folder with this name already exists.");
        }
        return Result<FolderDto>.Ok(new(folder.Id, folder.Name, linkCount));
    }

    private static string? Clean(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 100 ? null : trimmed;
    }
}
