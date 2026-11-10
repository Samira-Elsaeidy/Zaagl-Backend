using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Files.Domain;
using Zaagl.SharedKernel.Jobs;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>The <c>files</c> schema: the task table (BE-013) and the upload pipeline tables (ARC-009 section 3).</summary>
public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options) : DbContext(options)
{
    public const string Schema = "files";

    public DbSet<QueuedTask> Tasks => Set<QueuedTask>();
    public DbSet<UploadSessionRow> UploadSessions => Set<UploadSessionRow>();
    public DbSet<StorageObjectRow> StorageObjects => Set<StorageObjectRow>();
    public DbSet<BlockedHashRow> BlockedHashes => Set<BlockedHashRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.MapTaskTable();

        b.Entity<UploadSessionRow>(e =>
        {
            e.ToTable("upload_sessions", t =>
            {
                t.HasCheckConstraint("ck_upload_sessions_owner_xor_guest", "(owner_id IS NULL) <> (guest_creator_key IS NULL)");
                t.HasCheckConstraint("ck_upload_sessions_declared_length", "declared_length >= 1");
                t.HasCheckConstraint("ck_upload_sessions_received_length", "received_length BETWEEN 0 AND declared_length");
                t.HasCheckConstraint("ck_upload_sessions_status", "status IN (0, 1, 2, 3, 4)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DeclaredName).HasMaxLength(1024);
            e.Property(x => x.DeclaredType).HasMaxLength(255);
            e.Property(x => x.Status).HasConversion<short>();
            e.HasIndex(x => new { x.Status, x.ExpiresAt }).HasFilter("status IN (0, 1)").HasDatabaseName("ix_upload_sessions_status_expires");
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt }).IsDescending(false, true)
                .HasFilter("owner_id IS NOT NULL").HasDatabaseName("ix_upload_sessions_owner_created");
            e.HasIndex(x => new { x.OwnerId, x.CreateKeyHash }).IsUnique()
                .HasFilter("create_key_hash IS NOT NULL").HasDatabaseName("ux_upload_sessions_owner_create_key");
        });

        b.Entity<StorageObjectRow>(e =>
        {
            e.ToTable("storage_objects", t =>
            {
                t.HasCheckConstraint("ck_storage_objects_sha256", "sha256 IS NULL OR octet_length(sha256) = 32");
                t.HasCheckConstraint("ck_storage_objects_area", "area IN (0, 1, 2, 3, 4)");
                t.HasCheckConstraint("ck_storage_objects_scan", "scan_status IN (0, 1, 2, 3)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.ResourceId).IsUnique();
            e.Property(x => x.Area).HasConversion<short>();
            e.Property(x => x.ScanStatus).HasConversion<short>();
            e.Property(x => x.DisplayName).HasMaxLength(255).IsRequired();
            e.Property(x => x.DetectedMime).HasMaxLength(127);
            e.Property(x => x.DetectedExt).HasMaxLength(16);
            e.Property(x => x.ScanSignature).HasMaxLength(255);
            e.Property(x => x.ScanEngine).HasMaxLength(100);
            e.HasIndex(x => x.Sha256).HasFilter("sha256 IS NOT NULL").HasDatabaseName("ix_storage_objects_sha256");
        });

        b.Entity<BlockedHashRow>(e =>
        {
            e.ToTable("blocked_content_hashes", t => t.HasCheckConstraint("ck_blocked_content_hashes_sha256", "octet_length(sha256) = 32"));
            e.HasKey(x => x.Sha256);
            e.Property(x => x.Reason).IsRequired();
        });
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
