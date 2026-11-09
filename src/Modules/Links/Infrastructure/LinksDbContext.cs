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
    public DbSet<BlockedDestination> BlockedDestinations => Set<BlockedDestination>();
    public DbSet<BlockedAlias> BlockedAliases => Set<BlockedAlias>();
    public DbSet<AbuseEvidence> AbuseEvidence => Set<AbuseEvidence>();
    public DbSet<AbuseReport> AbuseReports => Set<AbuseReport>();
    public DbSet<AliasDenylistEntry> AliasDenylist => Set<AliasDenylistEntry>();
    public DbSet<AliasFreeze> AliasFreezes => Set<AliasFreeze>();
    public DbSet<LifecycleOutboxRow> LifecycleOutbox => Set<LifecycleOutboxRow>();

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
            // C1 (ARC-002 §1): destination/host are null exactly for content kinds; enforced by the checks below.
            e.Property(x => x.DestinationUrl).HasMaxLength(2048).IsRequired(false);
            e.Property(x => x.TargetHost).HasMaxLength(255).IsRequired(false);
            e.Property(x => x.TargetKind).HasConversion<short>().HasDefaultValue(Contracts.LinkTargetKind.Url).HasSentinel(Contracts.LinkTargetKind.Url);
            e.ToTable(t => t.HasCheckConstraint("ck_links_target_kind", "target_kind IN (0, 1, 2, 3)"));
            e.ToTable(t => t.HasCheckConstraint("ck_links_target_resource", "(target_kind = 0) = (resource_id IS NULL)"));
            e.ToTable(t => t.HasCheckConstraint("ck_links_target_destination",
                "(target_kind = 0) = (destination_url IS NOT NULL AND target_host IS NOT NULL)"));
            // ux_links_resource_id (unique, partial) is created CONCURRENTLY by migration AddLinkResourceIndex.
            e.HasIndex(x => x.ResourceId).IsUnique().HasFilter("resource_id IS NOT NULL").HasDatabaseName("ux_links_resource_id");
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
            e.HasIndex(x => x.TargetHost);
            e.HasIndex(x => x.SafetyCheckedAt);
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
            e.ToTable(t => t.HasCheckConstraint("ck_access_policies_link_key_hash",
                "link_key_hash IS NULL OR octet_length(link_key_hash) = 32"));
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

        b.Entity<BlockedDestination>(e =>
        {
            e.ToTable("blocked_destinations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Pattern).HasMaxLength(255).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.Kind, x.Pattern }).IsUnique();
        });

        b.Entity<BlockedAlias>(e =>
        {
            e.ToTable("blocked_aliases");
            e.HasKey(x => x.SlugKey);
            e.Property(x => x.SlugKey).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        });

        b.Entity<AbuseEvidence>(e =>
        {
            e.ToTable("abuse_evidence");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetUrl).HasMaxLength(2048).IsRequired(false);
            e.Property(x => x.DisabledKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => x.LinkId);
            e.HasIndex(x => x.KeepUntil);
        });

        b.Entity<AbuseReport>(e =>
        {
            e.ToTable("abuse_reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Category).HasMaxLength(50).IsRequired();
            e.Property(x => x.Details).HasMaxLength(2000);
            e.Property(x => x.ReporterEmail).HasMaxLength(255);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Resolution).HasMaxLength(2000);
            e.HasIndex(x => x.Status);
        });

        b.Entity<AliasDenylistEntry>(e =>
        {
            e.ToTable("alias_denylist");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Pattern).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(10).IsRequired();
            e.HasIndex(x => new { x.Kind, x.Pattern }).IsUnique();
        });

        // C10 (contracts.md): content-link lifecycle outbox, drained by the worker (BE-019).
        b.Entity<LifecycleOutboxRow>(e =>
        {
            e.ToTable("lifecycle_outbox", t => t.HasCheckConstraint("ck_lifecycle_outbox_event", "event IN (1, 2, 3, 4, 5, 6)"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Kind).HasConversion<short>();
            e.Property(x => x.Event).HasConversion<short>();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.OccurredAt).HasFilter("processed_at IS NULL").HasDatabaseName("ix_lifecycle_outbox_pending");
            e.HasIndex(x => x.LinkId);
        });

        b.Entity<AliasFreeze>(e =>
        {
            e.ToTable("alias_freezes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Source).HasMaxLength(50).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.StartsAt, x.Until });
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
