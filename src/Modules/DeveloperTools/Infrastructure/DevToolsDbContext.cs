using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.DeveloperTools.Domain;

namespace Zaagl.Modules.DeveloperTools.Infrastructure;

/// <summary>The <c>devtools</c> schema (docs/phase2), owned by the DeveloperTools module only.</summary>
public sealed class DevToolsDbContext(DbContextOptions<DevToolsDbContext> options) : DbContext(options)
{
    public const string Schema = "devtools";

    public DbSet<SecretRow> Secrets => Set<SecretRow>();
    public DbSet<SecretEventRow> SecretEvents => Set<SecretEventRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<SecretRow>(e =>
        {
            e.ToTable("secrets", t =>
            {
                // The largest plan cap (ToolCeilings.VaultCiphertextBytes); the per-plan cap is enforced in the application.
                t.HasCheckConstraint("ck_secrets_ciphertext_bytes", "ciphertext_bytes BETWEEN 30 AND 1048576");
                t.HasCheckConstraint("ck_secrets_ciphertext_length", "ciphertext IS NULL OR octet_length(ciphertext) = ciphertext_bytes");
                t.HasCheckConstraint("ck_secrets_envelope_version", "envelope_version BETWEEN 1 AND 255");
                t.HasCheckConstraint("ck_secrets_max_views", "max_views BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_secrets_views_used", "views_used BETWEEN 0 AND max_views");
                t.HasCheckConstraint("ck_secrets_wiped", "(ciphertext IS NULL) = (wiped_at IS NOT NULL)");
                t.HasCheckConstraint("ck_secrets_wipe_reason",
                    "wipe_reason IS NULL OR wipe_reason IN ('burned', 'deleted', 'expired', 'disabled', 'purged')");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.WipeReason).HasMaxLength(16);
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt }).HasFilter("owner_id IS NOT NULL").HasDatabaseName("ix_secrets_owner");
            // The wipe sweep: live ciphertext whose expiry passed.
            e.HasIndex(x => x.ExpiresAt).HasFilter("ciphertext IS NOT NULL").HasDatabaseName("ix_secrets_live_expiry");
        });

        b.Entity<SecretEventRow>(e =>
        {
            e.ToTable("secret_events", t => t.HasCheckConstraint("ck_secret_events_kind",
                "kind IN ('created', 'revealed', 'burned', 'deleted', 'expired', 'disabled', 'purged')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.HasIndex(x => new { x.ResourceId, x.Id });
        });
    }
}

public sealed class DevToolsDbContextFactory : IDesignTimeDbContextFactory<DevToolsDbContext>
{
    public DevToolsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<DevToolsDbContext>();
        DeveloperToolsModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new DevToolsDbContext(builder.Options);
    }
}
