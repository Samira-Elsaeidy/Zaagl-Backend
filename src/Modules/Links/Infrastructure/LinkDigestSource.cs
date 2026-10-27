using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Modules.Links.Infrastructure;

/// <summary>
/// Spec §7.10's three qualification rules, literally:
/// - expiring: active, expires_at-7d &lt;= now &lt; expires_at, created more than 7 days before expiry
///   (a link created with a shorter-than-7-day expiry skips this stage entirely), not yet queued.
/// - expired: quarantined, not yet queued.
/// - purge: quarantined, purge_after-7d &lt;= now &lt; purge_after, not yet queued.
/// Guest links (no owner) never qualify - they get no emails (spec §7.12).
/// </summary>
public sealed class LinkDigestSource(LinksDbContext db) : ILinkDigestSource
{
    public async Task<IReadOnlyList<DigestCandidate>> GetCandidatesAsync(string stage, DateTime now, CancellationToken ct)
    {
        var q = db.Links.AsNoTracking().Where(l => l.OwnerId != null && l.DeletedAt == null && l.DisabledAt == null);

        q = stage switch
        {
            DigestStages.Expiring => q.Where(l =>
                l.ExpiringNoticeQueuedAt == null
                && l.Policy!.ExpiresAt != null
                && l.Policy.ExpiresAt.Value.AddDays(-7) <= now && now < l.Policy.ExpiresAt.Value
                && l.Policy.ExpiresAt.Value.AddDays(-7) > l.CreatedAt
                && (l.PurgeAfter == null || l.PurgeAfter > now)),

            DigestStages.Expired => q.Where(l =>
                l.ExpiredNoticeQueuedAt == null
                && l.Policy!.ExpiresAt != null && l.Policy.ExpiresAt <= now
                && (l.PurgeAfter == null || l.PurgeAfter > now)),

            DigestStages.Purge => q.Where(l =>
                l.PurgeNoticeQueuedAt == null
                && l.Policy!.ExpiresAt != null && l.Policy.ExpiresAt <= now
                && l.PurgeAfter != null
                && l.PurgeAfter.Value.AddDays(-7) <= now && now < l.PurgeAfter.Value),

            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

        return await q.Select(l => new DigestCandidate(l.Id, l.OwnerId!.Value, l.Slug, l.Policy!.ExpiresAt!.Value)).ToListAsync(ct);
    }

    public async Task MarkQueuedAsync(string stage, IReadOnlyList<Guid> linkIds, DateTime now, CancellationToken ct)
    {
        if (linkIds.Count == 0) return;
        var q = db.Links.Where(l => linkIds.Contains(l.Id));
        await (stage switch
        {
            DigestStages.Expiring => q.ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiringNoticeQueuedAt, now), ct),
            DigestStages.Expired => q.ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiredNoticeQueuedAt, now), ct),
            DigestStages.Purge => q.ExecuteUpdateAsync(s => s.SetProperty(l => l.PurgeNoticeQueuedAt, now), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        });
    }
}
