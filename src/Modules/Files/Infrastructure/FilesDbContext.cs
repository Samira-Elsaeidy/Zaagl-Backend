using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>The <c>files</c> schema. BE-013 adds the task table (ARC-012 §3: scan, promote, delete-bytes,
/// quarantine, upload expiry); upload sessions, stored objects and the deletion outbox follow in BE-011/BE-012.</summary>
public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options) : DbContext(options)
{
    public const string Schema = "files";

    public DbSet<QueuedTask> Tasks => Set<QueuedTask>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.MapTaskTable();
    }
}

public sealed class FilesDbContextFactory : IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<FilesDbContext>();
        FilesModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new FilesDbContext(builder.Options);
    }
}
