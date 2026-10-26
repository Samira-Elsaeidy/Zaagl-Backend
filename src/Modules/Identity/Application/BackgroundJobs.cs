using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;

namespace Zaagl.Modules.Identity.Application;

/// <summary>Spec §7.4: one Admin account exists from config at first start, pre-confirmed so it can sign
/// in immediately (it still needs to enrol TOTP before it gets a full session - see IdentityService).</summary>
public sealed class AdminBootstrapHostedService(IServiceScopeFactory scopes, IOptions<AdminBootstrapOptions> options, ILogger<AdminBootstrapHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrWhiteSpace(o.Password))
        {
            logger.LogInformation("AdminBootstrap:Email/Password not configured; skipping admin bootstrap.");
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var email = o.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;

        db.Users.Add(new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(o.Password),
            DisplayName = "Admin",
            EmailConfirmed = true,
            Role = "Admin",
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Bootstrapped admin account {Email}.", email);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Spec §7.4/§7.8: unconfirmed accounts older than Accounts:UnconfirmedTtlDays (default 7) are
/// deleted daily - they own nothing else yet (no links, no keys), so a straight delete is safe.</summary>
public sealed class UnconfirmedAccountCleanupJob(IServiceScopeFactory scopes, IConfiguration config, ILogger<UnconfirmedAccountCleanupJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ttlDays = config.GetValue<int?>("Accounts:UnconfirmedTtlDays") ?? 7;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                var cutoff = DateTime.UtcNow.AddDays(-ttlDays);
                var deleted = await db.Users
                    .Where(u => !u.EmailConfirmed && u.PasswordHash != null && u.CreatedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Deleted {Count} unconfirmed accounts older than {Days} days.", deleted, ttlDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unconfirmed-account cleanup failed.");
            }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
