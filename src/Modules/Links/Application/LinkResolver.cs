using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Modules.Links.Application;

/// <summary>Read side used by the redirect path. Snapshots are cached; the click counter is not.</summary>
public sealed class LinkResolver(LinksDbContext db, RedirectsDataSource redirects, HybridCache cache) : ILinkResolver, ILinkDirectory
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

    // Spec §7.0: the redirect hot path never touches the "main" EF pool - it reads from a small,
    // fast-timeout pool of its own (RedirectsDataSource), via raw SQL, so a slow creation transaction can
    // never queue behind (or starve) a redirect lookup.
    private async ValueTask<LinkSnapshot?> LoadAsync(string slugKey, CancellationToken ct)
    {
        await using var conn = await redirects.Value.OpenConnectionAsync(ct);

        Guid id, ownerId;
        string slug, destinationUrl;
        bool isActive, publicStats;
        string? passwordHash, fallbackUrl, ogTitle, ogDescription, ogImageUrl, disabledKind;
        DateTime? expiresAt, purgeAfter, deletedAt, disabledAt;
        int? maxClicks;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT l.id, l.owner_id, l.slug, l.destination_url, l.is_active, l.public_stats,
                       l.og_title, l.og_description, l.og_image_url,
                       l.purge_after, l.deleted_at, l.disabled_at, l.disabled_kind,
                       p.password_hash, p.expires_at, p.max_clicks, p.fallback_url
                FROM links.links l
                LEFT JOIN links.access_policies p ON p.link_id = l.id
                WHERE l.slug_key = $1
                """;
            cmd.Parameters.AddWithValue(slugKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            id = reader.GetGuid(0);
            ownerId = reader.GetGuid(1);
            slug = reader.GetString(2);
            destinationUrl = reader.GetString(3);
            isActive = reader.GetBoolean(4);
            publicStats = reader.GetBoolean(5);
            ogTitle = reader.IsDBNull(6) ? null : reader.GetString(6);
            ogDescription = reader.IsDBNull(7) ? null : reader.GetString(7);
            ogImageUrl = reader.IsDBNull(8) ? null : reader.GetString(8);
            purgeAfter = reader.IsDBNull(9) ? null : reader.GetDateTime(9);
            deletedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10);
            disabledAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11);
            disabledKind = reader.IsDBNull(12) ? null : reader.GetString(12);
            passwordHash = reader.IsDBNull(13) ? null : reader.GetString(13);
            expiresAt = reader.IsDBNull(14) ? null : reader.GetDateTime(14);
            maxClicks = reader.IsDBNull(15) ? null : reader.GetInt32(15);
            fallbackUrl = reader.IsDBNull(16) ? null : reader.GetString(16);
        }

        var rules = new List<RuleDto>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT kind, match, url, weight, priority FROM links.destinations
                WHERE link_id = $1 ORDER BY priority, id
                """;
            cmd.Parameters.AddWithValue(id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rules.Add(new RuleDto(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)));
        }

        return new LinkSnapshot(
            id, ownerId, slug, destinationUrl, isActive, passwordHash, expiresAt, maxClicks, fallbackUrl,
            ogTitle, ogDescription, ogImageUrl, publicStats, rules, purgeAfter, deletedAt, disabledAt, disabledKind);
    }
}
