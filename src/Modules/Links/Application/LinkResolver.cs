using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Modules.Links.Application;

/// <summary>Read side used by the redirect path. Snapshots are cached; the click counter is not.</summary>
public sealed class LinkResolver(LinksDbContext db, HybridCache cache) : ILinkResolver, ILinkDirectory
{
    private static readonly HybridCacheEntryOptions SnapshotOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    public async Task<LinkSnapshot?> GetSnapshotAsync(string slugKey, CancellationToken cancellationToken)
    {
        var envelope = await cache.GetOrCreateAsync(
            LinkCache.Key(slugKey),
            async ct => new SnapshotEnvelope(await LoadAsync(slugKey, ct)),
            SnapshotOptions,
            cancellationToken: cancellationToken);
        return envelope.Snapshot;
    }

    public async Task<bool> TryConsumeClickAsync(Guid linkId, CancellationToken cancellationToken)
    {
        // Single conditional UPDATE: two concurrent visitors can never both take the last click.
        var rows = await db.AccessPolicies
            .Where(p => p.LinkId == linkId && p.MaxClicks != null && p.ClicksUsed < p.MaxClicks)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClicksUsed, p => p.ClicksUsed + 1), cancellationToken);
        return rows > 0;
    }

    public async Task<OwnedLink?> FindOwnedAsync(Guid userId, Guid linkId, CancellationToken cancellationToken) =>
        await db.Links.AsNoTracking()
            .Where(l => l.Id == linkId && l.OwnerId == userId)
            .Select(l => new OwnedLink(l.Id, l.Slug))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PublicLink?> FindPublicStatsAsync(string slug, CancellationToken cancellationToken)
    {
        var key = SlugRules.Key(slug);
        return await db.Links.AsNoTracking()
            .Where(l => l.SlugKey == key && l.PublicStats)
            .Select(l => new PublicLink(l.Id, l.Slug, l.DestinationUrl, l.Title))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async ValueTask<LinkSnapshot?> LoadAsync(string slugKey, CancellationToken ct)
    {
        var link = await db.Links.AsNoTracking()
            .Include(l => l.Policy).Include(l => l.Rules)
            .FirstOrDefaultAsync(l => l.SlugKey == slugKey, ct);
        if (link is null) return null;

        return new LinkSnapshot(
            link.Id,
            link.OwnerId,
            link.Slug,
            link.DestinationUrl,
            link.IsActive,
            link.Policy?.PasswordHash,
            link.Policy?.ExpiresAt,
            link.Policy?.MaxClicks,
            link.Policy?.FallbackUrl,
            link.OgTitle,
            link.OgDescription,
            link.OgImageUrl,
            link.PublicStats,
            link.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Id)
                .Select(r => new RuleDto(r.Kind, r.Match, r.Url, r.Weight, r.Priority)).ToList());
    }
}
