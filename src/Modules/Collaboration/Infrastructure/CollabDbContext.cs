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
