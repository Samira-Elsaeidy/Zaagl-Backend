using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Billing.Domain;

namespace Zaagl.Modules.Billing.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<ProcessedStripeEvent> ProcessedEvents => Set<ProcessedStripeEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Tier).HasConversion<int>();
            e.Property(x => x.StripeCustomerId).HasMaxLength(100);
            e.Property(x => x.StripeSubscriptionId).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(50);
            e.HasIndex(x => x.StripeCustomerId);
            e.HasIndex(x => x.StripeSubscriptionId);
        });

        b.Entity<ProcessedStripeEvent>(e =>
        {
            e.ToTable("processed_stripe_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasMaxLength(100);
        });
    }
}

public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<BillingDbContext>();
        BillingModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new BillingDbContext(builder.Options);
    }
}
