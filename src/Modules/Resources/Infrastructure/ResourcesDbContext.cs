using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Resources.Domain;

namespace Zaagl.Modules.Resources.Infrastructure;

/// <summary>The <c>resources</c> schema (ARC-006 §1), owned by the Resources module only.</summary>
public sealed class ResourcesDbContext(DbContextOptions<ResourcesDbContext> options) : DbContext(options)
{
    public const string Schema = "resources";

    public DbSet<ResourceRow> Resources => Set<ResourceRow>();
    public DbSet<QuotaLedgerRow> QuotaLedger => Set<QuotaLedgerRow>();
    public DbSet<QuotaReservationRow> QuotaReservations => Set<QuotaReservationRow>();
    public DbSet<QuotaUsageRow> QuotaUsage => Set<QuotaUsageRow>();
    public DbSet<DownloadGrantRow> DownloadGrants => Set<DownloadGrantRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<ResourceRow>(e =>
        {
            e.ToTable("resources", t =>
            {
                t.HasCheckConstraint("ck_resources_kind", "kind IN (1, 2, 3)");
                t.HasCheckConstraint("ck_resources_owner_xor_guest", "(owner_id IS NULL) <> (guest_creator_key IS NULL)");
                t.HasCheckConstraint("ck_resources_declared_size", "declared_size_bytes >= 0");
                t.HasCheckConstraint("ck_resources_size", "size_bytes IS NULL OR size_bytes >= 0");
                t.HasCheckConstraint("ck_resources_bytes_state", "bytes_state IN (0, 1, 2, 3)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PublicId).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.PublicId).IsUnique();
            e.Property(x => x.Kind).HasConversion<short>();
            e.Property(x => x.ProcessingStatus).HasConversion<short>().HasDefaultValue(Contracts.ProcessingStatus.Pending);
            e.Property(x => x.BytesState).HasConversion<short>().HasDefaultValue(BytesState.None);
            e.Property(x => x.CreatedVia).HasConversion<short>();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.RejectedReason).HasMaxLength(50);
            e.Property(x => x.DownloadCount).HasDefaultValue(0L);
            e.HasIndex(x => x.LinkId).IsUnique();
            // Burn finaliser (ARC-019 section 7): claimed and still holding bytes.
            e.HasIndex(x => x.BurnClaimedAt).HasFilter("burn_claimed_at IS NOT NULL AND bytes_state = 1").HasDatabaseName("ix_resources_burn_claimed");
            // Purge and guest-expiry sweep (ARC-019 section 8).
            e.HasIndex(x => x.LinkPurgeAfter).HasFilter("bytes_state IN (0, 1)").HasDatabaseName("ix_resources_purge_due");
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt }).IsDescending(false, true)
                .HasFilter("owner_id IS NOT NULL").HasDatabaseName("ix_resources_owner_created");
            // Orphan sweeper (ARC-006 T19): resources never bound to a link.
            e.HasIndex(x => x.CreatedAt).HasFilter("link_id IS NULL").HasDatabaseName("ix_resources_unbound_created");
        });

        b.Entity<QuotaLedgerRow>(e =>
        {
            e.ToTable("quota_ledger", t =>
            {
                t.HasCheckConstraint("ck_quota_ledger_used", "used_bytes >= 0");
                t.HasCheckConstraint("ck_quota_ledger_reserved", "reserved_bytes >= 0");
            });
            e.HasKey(x => x.OwnerId);
            e.Property(x => x.OwnerId).ValueGeneratedNever();
        });

        b.Entity<QuotaReservationRow>(e =>
        {
            e.ToTable("quota_reservations", t =>
            {
                t.HasCheckConstraint("ck_quota_reservations_bytes", "bytes >= 0");
                t.HasCheckConstraint("ck_quota_reservations_state", "state IN (0, 1, 2)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.State).HasConversion<short>();
            e.HasIndex(x => new { x.OwnerId, x.State });
        });

        b.Entity<QuotaUsageRow>(e =>
        {
            e.ToTable("quota_usage", t => t.HasCheckConstraint("ck_quota_usage_bytes", "bytes >= 0"));
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.HasIndex(x => x.OwnerId);
        });

        b.Entity<DownloadGrantRow>(e =>
        {
            e.ToTable("download_grants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Purpose).HasConversion<short>();
            e.HasIndex(x => x.ResourceId);
            e.HasIndex(x => x.FirstUseBy).HasDatabaseName("ix_download_grants_first_use_by"); // S-4: grant cleanup
            e.HasOne<ResourceRow>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class ResourcesDbContextFactory : IDesignTimeDbContextFactory<ResourcesDbContext>
{
    public ResourcesDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ResourcesDbContext>();
        ResourcesModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new ResourcesDbContext(builder.Options);
    }
}
