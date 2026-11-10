using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Snippets.Domain;

namespace Zaagl.Modules.Snippets.Infrastructure;

/// <summary>The <c>snippets</c> schema (ARC-010 §2), owned by the Snippets module only.</summary>
public sealed class SnippetsDbContext(DbContextOptions<SnippetsDbContext> options) : DbContext(options)
{
    public const string Schema = "snippets";

    public DbSet<SnippetRow> Snippets => Set<SnippetRow>();
    public DbSet<SnippetVersionRow> Versions => Set<SnippetVersionRow>();
    public DbSet<SnippetIdempotencyRow> IdempotencyKeys => Set<SnippetIdempotencyRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<SnippetRow>(e =>
        {
            e.ToTable("snippets", t =>
            {
                t.HasCheckConstraint("ck_snippets_current_version", "current_version BETWEEN 1 AND 1000");
                t.HasCheckConstraint("ck_snippets_version_count", "version_count BETWEEN 1 AND 1000");
                t.HasCheckConstraint("ck_snippets_total_bytes", "total_bytes >= 0");
                // Versions are never removed individually (ARC-010 §3).
                t.HasCheckConstraint("ck_snippets_current_eq_count", "current_version = version_count");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
        });

        b.Entity<SnippetVersionRow>(e =>
        {
            e.ToTable("snippet_versions", t =>
            {
                t.HasCheckConstraint("ck_snippet_versions_version", "version >= 1");
                t.HasCheckConstraint("ck_snippet_versions_language", "language IS NULL OR language ~ '^[a-z0-9][a-z0-9+#._-]{0,63}$'");
                t.HasCheckConstraint("ck_snippet_versions_render_mode", "render_mode IN (0, 1, 2, 3)");
                // The largest plan cap; the per-plan cap is enforced in the application.
                t.HasCheckConstraint("ck_snippet_versions_size", "size_bytes BETWEEN 1 AND 2097152");
                t.HasCheckConstraint("ck_snippet_versions_line_count", "line_count >= 1");
                t.HasCheckConstraint("ck_snippet_versions_sha256", "octet_length(sha256) = 32");
            });
            e.HasKey(x => new { x.ResourceId, x.Version });
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Language).HasMaxLength(64);
            e.Property(x => x.RenderMode).HasConversion<short>();
            e.Property(x => x.CreatedVia).HasConversion<short>();
            e.Property(x => x.QuotaCommitted).HasDefaultValue(true);
            e.HasOne<SnippetRow>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
            // Repair job (ARC-010 §5): versions whose quota commit is still pending.
            e.HasIndex(x => x.CreatedAt).HasFilter("quota_committed = false").HasDatabaseName("ix_snippet_versions_uncommitted");
        });

        b.Entity<SnippetIdempotencyRow>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => new { x.Scope, x.Key });
            e.Property(x => x.Scope).HasMaxLength(80);
            e.Property(x => x.Key).HasMaxLength(128);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}

public sealed class SnippetsDbContextFactory : IDesignTimeDbContextFactory<SnippetsDbContext>
{
    public SnippetsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SnippetsDbContext>();
        SnippetsModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new SnippetsDbContext(builder.Options);
    }
}
