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
    public DbSet<HookRow> Hooks => Set<HookRow>();
    public DbSet<HookRequestRow> HookRequests => Set<HookRequestRow>();
    public DbSet<CapsuleRow> Capsules => Set<CapsuleRow>();

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

        b.Entity<HookRow>(e =>
        {
            e.ToTable("hooks", t =>
            {
                t.HasCheckConstraint("ck_hooks_token", "token ~ '^[A-Za-z0-9_-]{22}$'");
                t.HasCheckConstraint("ck_hooks_response_status", "response_status BETWEEN 200 AND 299 OR response_status BETWEEN 400 AND 599");
                t.HasCheckConstraint("ck_hooks_dates", "expires_at > created_at AND purge_after >= expires_at");
                t.HasCheckConstraint("ck_hooks_limits", "max_requests BETWEEN 1 AND 10000 AND max_body_bytes BETWEEN 0 AND 1048576 AND max_stored_bytes >= 0 AND ingest_per_minute >= 1");
                t.HasCheckConstraint("ck_hooks_counters", "window_count >= 0 AND received_total >= 0 AND stored_count >= 0 AND stored_bytes >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Token).HasMaxLength(22).UseCollation("C").IsRequired();
            e.HasIndex(x => x.Token).IsUnique().HasDatabaseName("uq_hooks_token");
            e.Property(x => x.Label).HasMaxLength(100);
            e.HasIndex(x => new { x.OwnerId, x.ExpiresAt }).HasDatabaseName("ix_hooks_owner_expiry");
            e.HasIndex(x => x.PurgeAfter).HasDatabaseName("ix_hooks_purge_after");
        });

        b.Entity<HookRequestRow>(e =>
        {
            e.ToTable("hook_requests", t =>
            {
                t.HasCheckConstraint("ck_hook_requests_sizes", "body_bytes_total >= 0 AND stored_bytes >= 0 AND (body IS NULL OR octet_length(body) <= 1048576)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Method).HasMaxLength(16).IsRequired();
            e.Property(x => x.Path).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Query).HasMaxLength(8192).IsRequired();
            e.Property(x => x.Headers).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(256);
            e.Property(x => x.SourceFingerprint).HasMaxLength(16).IsRequired();
            e.HasOne<HookRow>().WithMany().HasForeignKey(x => x.HookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HookId, x.Id }).HasDatabaseName("ix_hook_requests_hook");
        });

        b.Entity<CapsuleRow>(e =>
        {
            e.ToTable("capsules", t =>
            {
                t.HasCheckConstraint("ck_capsules_logs_bytes", "logs_bytes BETWEEN 0 AND 4194304");
                t.HasCheckConstraint("ck_capsules_counts", "console_count >= 0 AND error_count >= 0 AND network_count >= 0 AND failed_request_count >= 0");
            });
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.Logs).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.RecordingResourceId).HasFilter("recording_resource_id IS NOT NULL").HasDatabaseName("ix_capsules_recording");
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
