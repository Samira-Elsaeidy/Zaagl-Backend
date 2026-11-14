using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.DeveloperTools.Domain;
using Zaagl.Modules.DeveloperTools.Infrastructure;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Resources.Contracts;

namespace Zaagl.Modules.DeveloperTools.Application;

/// <summary>
/// The vault's reactions to link lifecycle events (C10, at least once, idempotent) and to the resource purge (C2 kind handler).
/// A secret's ciphertext is wiped as soon as its link is deleted, disabled, expired or purged; the rows themselves go when Resources
/// reclaims the resource at <c>purge_after</c> (guests: at expiry).
/// </summary>
public sealed class SecretLifecycle(DevToolsDbContext db, TimeProvider time) : ILinkLifecycleHandler, IResourceKindHandler
{
    public ResourceKind Kind => ResourceKind.Secret;

    public async Task HandleAsync(LinkLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        if (lifecycleEvent.Kind != LinkTargetKind.Secret) return;
        var id = lifecycleEvent.ResourceId;
        switch (lifecycleEvent)
        {
            case LinkRenewed renewed:
                await db.Secrets.Where(s => s.ResourceId == id && s.ExpiresAt < renewed.ExpiresAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, renewed.ExpiresAt), cancellationToken);
                break;
            case LinkDeleted:
                await WipeAsync(id, WipeReasons.Deleted, cancellationToken);
                break;
            case LinkDisabled:
                await WipeAsync(id, WipeReasons.Disabled, cancellationToken);
                break;
            case LinkExpired:
                await WipeAsync(id, WipeReasons.Expired, cancellationToken);
                break;
            case LinkPurged:
                await WipeAsync(id, WipeReasons.Purged, cancellationToken);
                break;
        }
    }

    /// <summary>C2: Resources reclaims the resource (purge, guest expiry, orphan): the secret's rows go. Idempotent.</summary>
    public async Task OnResourceDeletedAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        await db.SecretEvents.Where(e => e.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
        await db.Secrets.Where(s => s.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task WipeAsync(Guid resourceId, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var wiped = await db.Secrets.Where(s => s.ResourceId == resourceId && s.Ciphertext != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Ciphertext, (byte[]?)null)
                .SetProperty(x => x.WipedAt, now)
                .SetProperty(x => x.WipeReason, reason), ct);
        if (wiped == 0) return;
        var views = await db.Secrets.AsNoTracking().Where(s => s.ResourceId == resourceId).Select(s => s.ViewsUsed).FirstOrDefaultAsync(ct);
        db.SecretEvents.Add(new SecretEventRow { ResourceId = resourceId, Kind = reason, ViewsUsed = views, OccurredAt = now });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}

/// <summary>One DeveloperTools sweep (scoped). Registered in DI; <see cref="DevToolsSweeper"/> runs them all.</summary>
public interface IDevToolsSweep
{
    string Name { get; }

    /// <summary>One batch; returns how many items it handled. Idempotent and safe to run from several workers.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Vault: wipes the ciphertext of every secret whose link expired. It does not wait for the 90-day link quarantine
/// (docs/phase2/vault.md "Retention and wipe"); each wipe is a conditional UPDATE, so concurrent sweeps never double-count.</summary>
public sealed class VaultExpirySweep(DevToolsDbContext db, TimeProvider time) : IDevToolsSweep
{
    private const int Batch = 500;

    public string Name => "vault expiry wipe";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.Secrets.AsNoTracking().Where(s => s.Ciphertext != null && s.ExpiresAt <= now)
            .OrderBy(s => s.ExpiresAt).Select(s => s.ResourceId).Take(Batch).ToListAsync(ct);
        var done = 0;
        foreach (var id in ids)
        {
            var wiped = await db.Secrets.Where(s => s.ResourceId == id && s.Ciphertext != null && s.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Ciphertext, (byte[]?)null)
                    .SetProperty(x => x.WipedAt, now)
                    .SetProperty(x => x.WipeReason, WipeReasons.Expired), ct);
            if (wiped == 0) continue;
            var views = await db.Secrets.AsNoTracking().Where(s => s.ResourceId == id).Select(s => s.ViewsUsed).FirstOrDefaultAsync(ct);
            db.SecretEvents.Add(new SecretEventRow { ResourceId = id, Kind = SecretEventKinds.Expired, ViewsUsed = views, OccurredAt = now });
            done++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return done;
    }
}

/// <summary>
/// Runs every <see cref="IDevToolsSweep"/> (worker, every minute; tests call <see cref="RunOnceAsync"/>), each in its own scope and
/// isolated: one failure is logged and the others still run.
/// </summary>
public sealed class DevToolsSweeper(IServiceScopeFactory scopes, ILogger<DevToolsSweeper> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<string> names;
        await using (var probe = scopes.CreateAsyncScope())
            names = probe.ServiceProvider.GetServices<IDevToolsSweep>().Select(s => s.Name).ToList();

        var total = 0;
        foreach (var name in names)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sweep = scope.ServiceProvider.GetServices<IDevToolsSweep>().First(s => s.Name == name);
                var n = await sweep.RunAsync(ct);
                if (n > 0) logger.LogInformation("DeveloperTools sweep, {Step}: {Count}", name, n);
                total += n;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeveloperTools sweep step {Step} failed", name);
            }
        }
        return total;
    }
}

/// <summary>Worker only: runs <see cref="DevToolsSweeper"/> every minute.</summary>
public sealed class DevToolsMaintenanceJob(DevToolsSweeper sweeper, TimeProvider time, ILogger<DevToolsMaintenanceJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                await sweeper.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The DeveloperTools maintenance tick failed; retrying at the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
