using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Analytics.Domain;

namespace Zaagl.Modules.Analytics.Infrastructure;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public const string Schema = "analytics";

    public DbSet<ClickEventRow> ClickEvents => Set<ClickEventRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<ClickEventRow>(e =>
        {
            // The initial migration replaces the generated CREATE TABLE with a range-partitioned one.
            // A partitioned table's primary key must include the partition column.
            e.ToTable("click_events");
            e.HasKey(x => new { x.Id, x.OccurredAt });
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.VisitorHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReferrerHost).HasMaxLength(255);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Os).HasMaxLength(100);
            e.Property(x => x.Browser).HasMaxLength(100);
            e.Property(x => x.DeviceType).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.LinkId, x.OccurredAt });
            e.HasIndex(x => new { x.OwnerId, x.OccurredAt });
        });
    }
}

public sealed class AnalyticsDbContextFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AnalyticsDbContext>();
        AnalyticsModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new AnalyticsDbContext(builder.Options);
    }
}
