using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Collaboration.Domain;

namespace Zaagl.Modules.Collaboration.Infrastructure;

/// <summary>The <c>collab</c> schema (docs/phase2), owned by the Collaboration module only.</summary>
public sealed class CollabDbContext(DbContextOptions<CollabDbContext> options) : DbContext(options)
{
    public const string Schema = "collab";

    public DbSet<ComparisonRow> Comparisons => Set<ComparisonRow>();

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
