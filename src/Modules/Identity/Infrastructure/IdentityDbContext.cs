using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Zaagl.Modules.Identity.Domain;

namespace Zaagl.Modules.Identity.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginFailure> LoginFailures => Set<LoginFailure>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(50);
            e.Property(x => x.DisabledReason).HasMaxLength(500);
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<ExternalLogin>(e =>
        {
            e.ToTable("external_logins");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ProviderUserId).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<EmailToken>(e =>
        {
            e.ToTable("email_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Kind });
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<LoginFailure>(e =>
        {
            e.ToTable("login_failures");
            e.HasKey(x => x.Id);
            e.Property(x => x.IpPartition).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.UserId, x.IpPartition, x.CreatedAt });
        });
    }
}

/// <summary>Design-time only: lets `dotnet ef` build the model without a running database.</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityModule.Configure(builder, "Host=localhost;Database=zaagl");
        return new IdentityDbContext(builder.Options);
    }
}
