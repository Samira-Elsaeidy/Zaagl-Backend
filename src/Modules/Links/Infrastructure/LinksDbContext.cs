using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Links.Domain;

namespace Zaagl.Modules.Links.Infrastructure;

public sealed class LinksDbContext(DbContextOptions<LinksDbContext> options) : DbContext(options)
{
    public const string Schema = "links";

    public DbSet<Link> Links => Set<Link>();
    public DbSet<AccessPolicy> AccessPolicies => Set<AccessPolicy>();
    public DbSet<LinkDestination> Destinations => Set<LinkDestination>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.HasSequence<long>("slug_seq").StartsAt(1000);

        b.Entity<Link>(e =>
        {
            e.ToTable("links");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.SlugKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.DestinationUrl).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.OgTitle).HasMaxLength(200);
            e.Property(x => x.OgDescription).HasMaxLength(500);
            e.Property(x => x.OgImageUrl).HasMaxLength(2048);
            e.Property(x => x.DisabledKind).HasMaxLength(20);
            e.Property(x => x.DisabledReason).HasMaxLength(500);
            e.ToTable(t => t.HasCheckConstraint("ck_links_owner_xor_guest",
                "(owner_id IS NULL) <> (guest_creator_key IS NULL)"));
            e.HasIndex(x => x.SlugKey).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt });
            e.HasIndex(x => x.FolderId);
            e.HasIndex(x => x.Tags).HasMethod("gin");
            e.HasIndex(x => x.PurgeAfter);
            // Generated-link numbers are unique per length, mirroring spec §4.3's partial unique index.
            e.HasIndex(x => new { x.CodeLength, x.CodeNumber }).IsUnique()
                .HasFilter("\"code_number\" IS NOT NULL").HasDatabaseName("ix_links_code_number");
            e.ToTable(t => t.HasCheckConstraint("ck_links_disabled_kind_requires_at", "(disabled_at IS NULL) = (disabled_kind IS NULL)"));
            e.ToTable(t => t.HasCheckConstraint("ck_links_generated_columns",
                "(is_custom AND code_length IS NULL AND code_number IS NULL AND key_version IS NULL) " +
                "OR (NOT is_custom AND code_length IS NOT NULL AND code_number IS NOT NULL AND key_version IS NOT NULL)"));

            e.HasOne(x => x.Policy).WithOne().HasForeignKey<AccessPolicy>(p => p.LinkId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.LinkId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => new { x.Scope, x.Key });
            e.Property(x => x.Scope).HasMaxLength(80);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.ResponseBody).HasColumnType("jsonb");
        });

        b.Entity<AccessPolicy>(e =>
        {
            e.ToTable("access_policies");
            e.HasKey(x => x.LinkId);
            e.Property(x => x.LinkId).ValueGeneratedNever();
            e.Property(x => x.PasswordHash).HasMaxLength(100);
            e.Property(x => x.FallbackUrl).HasMaxLength(2048);
        });

        b.Entity<LinkDestination>(e =>
        {
            e.ToTable("destinations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Match).HasMaxLength(10);
            e.Property(x => x.Url).HasMaxLength(2048).IsRequired();
            e.HasIndex(x => x.LinkId);
        });

        b.Entity<Folder>(e =>
        {
            e.ToTable("folders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
        });
    }
}

public sealed class LinksDbContextFactory : IDesignTimeDbContextFactory<LinksDbContext>
{
    public LinksDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<LinksDbContext>();
        LinksModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new LinksDbContext(builder.Options);
    }
}
