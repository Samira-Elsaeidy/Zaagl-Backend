using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Modules.Links.Application;

/// <summary>Spec §7.14 account deletion, steps 1-3's Links-side work.</summary>
public sealed class AccountDataEraser(
    LinksDbContext db, IArchiveWriter archive, ILinkCacheInvalidator invalidator,
    IOptions<LinkOptions> options, TimeProvider time) : IAccountDataEraser
{
    private const int ChunkSize = 1000;

    public async Task<IReadOnlyList<string>> EraseOwnerLinksAsync(Guid ownerId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var touchedSlugKeys = new List<string>();
        var liveLinkIds = new List<Guid>();

        while (true)
        {
            var batch = await db.Links
                .Where(l => l.OwnerId == ownerId && l.DeletedAt == null)
                .OrderBy(l => l.Id).Take(ChunkSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var link in batch)
            {
                link.DeletedAt = now;
                var shortenedPurge = now.AddDays(options.Value.QuarantineDays);
                link.PurgeAfter = link.PurgeAfter is { } existing && existing < shortenedPurge ? existing : shortenedPurge;
                touchedSlugKeys.Add(link.SlugKey);
                liveLinkIds.Add(link.Id);
            }

            // expires_at = least(expires_at, now): needs the Policy rows too.
            var ids = batch.Select(l => l.Id).ToList();
            await db.AccessPolicies.Where(p => ids.Contains(p.LinkId) && (p.ExpiresAt == null || p.ExpiresAt > now))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, now), ct);

            await db.SaveChangesAsync(ct);

            if (batch.Count < ChunkSize) break;
        }

        if (touchedSlugKeys.Count > 0) await invalidator.NotifyChangedAsync(touchedSlugKeys, ct);
        await archive.DeleteAllForOwnerAsync(ownerId, liveLinkIds, ct);
        return touchedSlugKeys;
    }

    public async Task DeleteIdempotencyKeysAsync(Guid userId, CancellationToken ct) =>
        await db.IdempotencyKeys.Where(k => k.Scope == $"u:{userId}").ExecuteDeleteAsync(ct);
}
