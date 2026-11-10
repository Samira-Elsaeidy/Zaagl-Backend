using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

/// <summary>
/// C12 (BE-022): content modules ask Links to take a link down when a scan or the hash blocklist says so. It runs the Phase 0 routine of
/// spec 7.15 (<see cref="TakedownService"/>: disable, block the alias, write the evidence row, notify the cache) in the same unit as the
/// C10 <c>Disabled</c> outbox row, so Resources moves the bytes to quarantine. Idempotent: a link that is already taken down for abuse or
/// safety is left as it is. The hash is kept in the evidence reason (<c>sha256=hex</c>); retention and the legal hold are PD-11 (OPEN).
/// </summary>
public sealed class ContentLinkTakedown(LinksDbContext db, TakedownService takedowns, TimeProvider time) : ILinkTakedown
{
    public async Task<Result<bool>> TakeDownContentAsync(ContentTakedownRequest request, CancellationToken ct)
    {
        var link = await db.Links.Include(l => l.Policy)
            .FirstOrDefaultAsync(l => l.Id == request.LinkId && l.TargetKind != LinkTargetKind.Url, ct);
        if (link is null || link.ResourceId != request.ResourceId) return Error.NotFound("Link not found.");
        if (link.DisabledKind is DisabledKinds.Abuse or DisabledKinds.Safety) return Result<bool>.Ok(false);

        var kind = request.Kind == ContentTakedownKind.Safety ? DisabledKinds.Safety : DisabledKinds.Abuse;
        var reason = request.Sha256 is { Length: > 0 } hash ? $"{request.Reason}; sha256={Convert.ToHexStringLower(hash)}" : request.Reason;
        db.LifecycleOutbox.Add(new LifecycleOutboxRow
        {
            LinkId = link.Id, ResourceId = request.ResourceId, Kind = link.TargetKind, Event = LifecycleEventType.Disabled,
            Payload = JsonSerializer.Serialize(new { DisabledKind = kind }), OccurredAt = time.GetUtcNow().UtcDateTime,
        });
        await takedowns.TakeDownAsync(link, kind, reason, ct); // saves the link, the evidence and the outbox row together
        return Result<bool>.Ok(true);
    }
}

/// <summary>
/// Drains <c>links.lifecycle_outbox</c> (C10) into every <see cref="ILinkLifecycleHandler"/>, in id order, at least once. A handler that
/// throws leaves the row pending with its attempt counted; handlers are idempotent so a repeat is harmless.
/// </summary>
public sealed class LifecycleOutboxDrain(LinksDbContext db, IEnumerable<ILinkLifecycleHandler> handlers, TimeProvider time, ILogger<LifecycleOutboxDrain> logger)
{
    private const int Batch = 100;
    private const int MaxAttempts = 20;

    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var rows = await db.LifecycleOutbox.Where(r => r.ProcessedAt == null && r.Attempts < MaxAttempts)
            .OrderBy(r => r.Id).Take(Batch).ToListAsync(ct);
        var done = 0;
        foreach (var row in rows)
        {
            try
            {
                var lifecycleEvent = ToEvent(row);
                if (lifecycleEvent is not null)
                    foreach (var handler in handlers) await handler.HandleAsync(lifecycleEvent, ct);
                row.ProcessedAt = time.GetUtcNow().UtcDateTime;
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Attempts++;
                row.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                logger.LogWarning(ex, "Lifecycle event {OutboxId} failed (attempt {Attempts})", row.Id, row.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
        return done;
    }

    internal static LinkLifecycleEvent? ToEvent(LifecycleOutboxRow r)
    {
        JsonElement payload = default;
        if (!string.IsNullOrEmpty(r.Payload)) payload = JsonDocument.Parse(r.Payload).RootElement.Clone();
        string? Str(string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        DateTime Date(string name) => payload.TryGetProperty(name, out var v) ? v.GetDateTime().ToUniversalTime() : r.OccurredAt;
        return r.Event switch
        {
            LifecycleEventType.Deleted => new LinkDeleted(r.Id, r.LinkId, r.ResourceId, r.Kind, r.OccurredAt),
            LifecycleEventType.Disabled => new LinkDisabled(r.Id, r.LinkId, r.ResourceId, r.Kind, r.OccurredAt, Str("DisabledKind") ?? "admin"),
            LifecycleEventType.Enabled => new LinkEnabled(r.Id, r.LinkId, r.ResourceId, r.Kind, r.OccurredAt),
            LifecycleEventType.Renewed => new LinkRenewed(r.Id, r.LinkId, r.ResourceId, r.Kind, r.OccurredAt, Date("ExpiresAt"), Date("PurgeAfter")),
            LifecycleEventType.Expired => new LinkExpired(r.Id, r.LinkId, r.ResourceId, r.Kind, r.OccurredAt,
                payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("IsGuest", out var g) && g.ValueKind == JsonValueKind.True),
            LifecycleEventType.Purged => new LinkPurged(r.Id, r.LinkId, r.ResourceId, r.Kind, r.OccurredAt),
            _ => null,
        };
    }
}

/// <summary>Runs <see cref="LifecycleOutboxDrain"/> every 2 seconds (ARC-012 section 4). Hosted in the Api, which carries Links and Resources
/// together; the worker has no Links module.</summary>
public sealed class LifecycleOutboxDrainJob(IServiceScopeFactory scopes, ILogger<LifecycleOutboxDrainJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LifecycleOutboxDrain>().DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The lifecycle outbox drain failed; retrying at the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
