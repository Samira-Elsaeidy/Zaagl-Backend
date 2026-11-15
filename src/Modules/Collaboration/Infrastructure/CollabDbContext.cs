using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Collaboration.Domain;

namespace Zaagl.Modules.Collaboration.Infrastructure;

/// <summary>The <c>collab</c> schema (docs/phase2), owned by the Collaboration module only.</summary>
public sealed class CollabDbContext(DbContextOptions<CollabDbContext> options) : DbContext(options)
{
    public const string Schema = "collab";

    public DbSet<ComparisonRow> Comparisons => Set<ComparisonRow>();
    public DbSet<CommentSettingsRow> CommentSettings => Set<CommentSettingsRow>();
    public DbSet<CommentRow> Comments => Set<CommentRow>();
    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();
    public DbSet<DropPortalRow> DropPortals => Set<DropPortalRow>();
    public DbSet<DropFileRow> DropFiles => Set<DropFileRow>();
    public DbSet<TableRow> Tables => Set<TableRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<ComparisonRow>(e =>
        {
            e.ToTable("comparisons", t =>
            {
                t.HasCheckConstraint("ck_comparisons_distinct_parts", "before_resource_id <> after_resource_id");
                t.HasCheckConstraint("ck_comparisons_labels", "(before_label IS NULL OR char_length(before_label) <= 40) AND (after_label IS NULL OR char_length(after_label) <= 40)");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.BeforeLabel).HasMaxLength(40);
            e.Property(x => x.AfterLabel).HasMaxLength(40);
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt }).HasDatabaseName("ix_comparisons_owner");
        });

        b.Entity<CommentSettingsRow>(e =>
        {
            e.ToTable("comment_settings", t => t.HasCheckConstraint("ck_comment_settings_count", "comment_count BETWEEN 0 AND 10000"));
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
        });

        b.Entity<CommentRow>(e =>
        {
            e.ToTable("comments", t =>
            {
                t.HasCheckConstraint("ck_comments_point", "x BETWEEN 0 AND 1 AND y BETWEEN 0 AND 1");
                t.HasCheckConstraint("ck_comments_side", "side IS NULL OR side IN (0, 1)");
                t.HasCheckConstraint("ck_comments_body", "char_length(body) BETWEEN 1 AND 1000");
                t.HasCheckConstraint("ck_comments_author", "author_kind IN (0, 1, 2) AND ((author_kind = 2) = (author_user_id IS NULL))");
                t.HasCheckConstraint("ck_comments_status", "status IN (0, 1) AND ((status = 1) = (resolved_at IS NOT NULL))");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Body).HasMaxLength(1000).IsRequired();
            e.Property(x => x.AuthorName).HasMaxLength(60);
            e.HasOne<CommentSettingsRow>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ResourceId, x.Id }).HasDatabaseName("ix_comments_resource");
            e.HasIndex(x => x.AuthorUserId).HasFilter("author_user_id IS NOT NULL").HasDatabaseName("ix_comments_author");
        });

        b.Entity<ApprovalRow>(e =>
        {
            e.ToTable("approvals", t =>
            {
                t.HasCheckConstraint("ck_approvals_state", "state IN (0, 1, 2, 3)");
                // The state machine as data invariants: an approved or paid draft has its timestamp; released has its own.
                t.HasCheckConstraint("ck_approvals_approved", "(state <> 1) OR approved_at IS NOT NULL");
                t.HasCheckConstraint("ck_approvals_paid", "(state <> 2) OR paid_at IS NOT NULL");
                t.HasCheckConstraint("ck_approvals_released", "(state = 3) = (released_at IS NOT NULL)");
                t.HasCheckConstraint("ck_approvals_watermark", "char_length(watermark_text) BETWEEN 1 AND 60");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.WatermarkText).HasMaxLength(60).IsRequired();
            e.Property(x => x.WatermarkKind).HasMaxLength(32).IsRequired();
            e.Property(x => x.ApproverName).HasMaxLength(100);
            e.Property(x => x.ApproverEmail).HasMaxLength(254);
            e.Property(x => x.ApprovalNote).HasMaxLength(1000);
            e.Property(x => x.PaymentProvider).HasMaxLength(32);
            e.Property(x => x.PaymentReference).HasMaxLength(128);
            e.HasIndex(x => x.DraftResourceId).HasDatabaseName("ix_approvals_draft");
        });

        b.Entity<DropPortalRow>(e =>
        {
            e.ToTable("drop_portals", t =>
            {
                t.HasCheckConstraint("ck_drop_portals_caps", "max_files BETWEEN 1 AND 10000 AND max_bytes >= 1 AND max_file_bytes >= 1");
                t.HasCheckConstraint("ck_drop_portals_instructions", "instructions IS NULL OR char_length(instructions) <= 2000");
                t.HasCheckConstraint("ck_drop_portals_types", "char_length(allowed_types) <= 100");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.Instructions).HasMaxLength(2000);
            e.Property(x => x.AllowedTypes).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt }).HasDatabaseName("ix_drop_portals_owner");
        });

        b.Entity<DropFileRow>(e =>
        {
            e.ToTable("drop_files", t =>
            {
                t.HasCheckConstraint("ck_drop_files_name", "uploader_name IS NULL OR char_length(uploader_name) <= 100");
                t.HasCheckConstraint("ck_drop_files_email", "uploader_email IS NULL OR char_length(uploader_email) <= 254");
                t.HasCheckConstraint("ck_drop_files_note", "note IS NULL OR char_length(note) <= 1000");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.UploaderName).HasMaxLength(100);
            e.Property(x => x.UploaderEmail).HasMaxLength(254);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.PortalId, x.ReceivedAt }).HasDatabaseName("ix_drop_files_portal");
            e.HasIndex(x => x.UploadSessionId).IsUnique().HasDatabaseName("ux_drop_files_session");
        });

        b.Entity<TableRow>(e =>
        {
            e.ToTable("tables", t =>
            {
                t.HasCheckConstraint("ck_tables_format", "source_format IN ('json', 'csv')");
                t.HasCheckConstraint("ck_tables_size", "column_count BETWEEN 1 AND 500 AND row_count BETWEEN 0 AND 200000 AND document_bytes BETWEEN 2 AND 33554432");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.SourceFormat).HasMaxLength(4).IsRequired();
            e.Property(x => x.Document).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt }).HasFilter("owner_id IS NOT NULL").HasDatabaseName("ix_tables_owner");
        });
    }
}

public sealed class CollabDbContextFactory : IDesignTimeDbContextFactory<CollabDbContext>
{
    public CollabDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<CollabDbContext>();
        CollaborationModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new CollabDbContext(builder.Options);
    }
}
