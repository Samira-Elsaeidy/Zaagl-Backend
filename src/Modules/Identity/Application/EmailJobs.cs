using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Modules.Identity.Application;

/// <summary>Small payload shapes stored in <see cref="EmailOutbox.PayloadJson"/>.</summary>
internal sealed record ConfirmEmailPayload(string Token);
internal sealed record PasswordResetPayload(string Token);
internal sealed record SecurityNoticePayload(string Message);

/// <summary>Queueing helpers used by IdentityService (account emails) and EmailDigestJob (link warnings).
/// Kept as plain static methods against the DbContext, same shape as GuestQuotaStore's raw-SQL helpers.</summary>
public static class EmailQueue
{
    public static async Task QueueAccountEmailAsync(IdentityDbContext db, Guid? userId, string kind, object? payload, string? recipientEmail, CancellationToken ct)
    {
        db.EmailOutbox.Add(new EmailOutbox
        {
            Kind = kind,
            UserId = userId,
            RecipientEmail = recipientEmail,
            PayloadJson = JsonSerializer.Serialize(payload ?? new { }),
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Spec §7.10's dispatcher: claims a batch every 30s (short claim transaction, then processes outside any
/// transaction), renders and sends, and applies the retry/backoff/suppression rules. At-least-once
/// delivery; a rare duplicate on a crash between send and the "sent" commit is accepted (spec's own text).
/// </summary>
public sealed class EmailDispatcher(
    IServiceScopeFactory scopes, IEmailSender sender, IOptions<EmailOptions> options, TimeProvider clock, ILogger<EmailDispatcher> logger)
    : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do { await RunOnceAsync(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;

        List<Guid> ids;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            ids = await db.Database.SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM identity.email_outbox
                WHERE (status = 'pending' AND send_after <= {now}) OR (status = 'sending' AND locked_until < {now})
                ORDER BY id LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);

            if (ids.Count > 0)
                await db.EmailOutbox.Where(e => ids.Contains(e.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Status, EmailOutboxStatuses.Sending)
                        .SetProperty(e => e.LockedUntil, now.AddMinutes(5)), ct);

            await tx.CommitAsync(ct);
        }

        foreach (var id in ids) await ProcessOneAsync(db, id, now, ct);
    }

    private async Task ProcessOneAsync(IdentityDbContext db, Guid id, DateTime now, CancellationToken ct)
    {
        var row = await db.EmailOutbox.Include(e => e.Items).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row is null) return;

        var user = row.UserId is { } uid ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct) : null;
        var toEmail = row.RecipientEmail ?? user?.Email;

        // Step 1: skip and cancel if suppressed, or the user is gone/disabled (security-notice is exempt -
        // it carries its own address and must still reach the owner after the account row is tombstoned).
        var userUnavailable = row.Kind != EmailKinds.SecurityNotice && (user is null || user.DisabledAt is not null);
        if (toEmail is null || userUnavailable || await db.EmailSuppressions.AnyAsync(s => s.Email == toEmail, ct))
        {
            row.Status = EmailOutboxStatuses.Cancelled;
            await db.SaveChangesAsync(ct);
            return;
        }

        // Step 2: for digest kinds, re-validate every item right before rendering.
        if (row.Kind is EmailKinds.Expiring or EmailKinds.Expired or EmailKinds.Purge)
        {
            var live = await RevalidateDigestItemsAsync(db, row, ct);
            if (live.Count == 0)
            {
                row.Status = EmailOutboxStatuses.Cancelled;
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        EmailMessage message;
        try
        {
            message = BuildMessage(row, toEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to render outbox row {Id} (kind {Kind}); marking failed, not retried.", row.Id, row.Kind);
            row.Status = EmailOutboxStatuses.Failed;
            row.LastError = Truncate(ex.Message);
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            await sender.SendAsync(message, ct);
        }
        catch (EmailPermanentFailureException ex)
        {
            row.Status = EmailOutboxStatuses.Failed;
            row.LastError = Truncate(ex.Message);
            if (!await db.EmailSuppressions.AnyAsync(s => s.Email == toEmail, ct))
                db.EmailSuppressions.Add(new EmailSuppression { Email = toEmail, Reason = "permanent-failure" });
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.Attempts++;
            row.LastError = Truncate(ex.Message);
            if (now - row.CreatedAt > GiveUpAfter)
            {
                row.Status = EmailOutboxStatuses.Failed;
            }
            else
            {
                row.Status = EmailOutboxStatuses.Pending;
                row.SendAfter = now + (row.Attempts <= Backoff.Length ? Backoff[row.Attempts - 1] : TimeSpan.FromHours(6));
                row.LockedUntil = null;
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        row.Status = EmailOutboxStatuses.Sent;
        row.SentAt = now;
        row.LockedUntil = null;
        if (row.Kind == EmailKinds.SecurityNotice) row.RecipientEmail = null; // spec §7.14: cleared after send
        await db.SaveChangesAsync(ct);
    }

    private static async Task<List<EmailOutboxItem>> RevalidateDigestItemsAsync(IdentityDbContext db, EmailOutbox row, CancellationToken ct)
    {
        // Best effort: a renewal committing between this check and SMTP acceptance can still yield one
        // outdated warning (spec's own accepted gap - no lock across the SMTP call).
        var live = new List<EmailOutboxItem>();
        foreach (var item in row.Items)
        {
            var current = await db.Database.SqlQuery<DateTime?>(
                $"""SELECT p.expires_at AS "Value" FROM links.links l LEFT JOIN links.access_policies p ON p.link_id = l.id WHERE l.id = {item.LinkId} AND l.deleted_at IS NULL""")
                .FirstOrDefaultAsync(ct);
            if (current is { } expiresAt && expiresAt == item.ExpiresAtSnapshot) live.Add(item);
        }
        return live;
    }

    private EmailMessage BuildMessage(EmailOutbox row, string toEmail)
    {
        var opts = options.Value;
        return row.Kind switch
        {
            EmailKinds.ConfirmEmail => EmailTemplates.ConfirmEmail(toEmail, BuildFragmentUrl(opts, "/account/confirm-email", row.UserId!.Value, Parse<ConfirmEmailPayload>(row).Token)),
            EmailKinds.PasswordReset => EmailTemplates.PasswordReset(toEmail, BuildFragmentUrl(opts, "/account/reset-password", row.UserId!.Value, Parse<PasswordResetPayload>(row).Token)),
            EmailKinds.AccountExists => EmailTemplates.AccountExists(toEmail),
            EmailKinds.SecurityNotice => EmailTemplates.SecurityNotice(toEmail, Parse<SecurityNoticePayload>(row).Message),
            _ => EmailTemplates.Digest(toEmail, row.Kind, row.Items.Select(i => (i.Slug, i.ExpiresAtSnapshot)).ToList()),
        };
    }

    private static string BuildFragmentUrl(EmailOptions opts, string path, Guid userId, string token) =>
        $"{opts.ApiBaseUrl.TrimEnd('/')}{path}#u={userId}&t={Uri.EscapeDataString(token)}";

    private static T Parse<T>(EmailOutbox row) => JsonSerializer.Deserialize<T>(row.PayloadJson)
        ?? throw new InvalidOperationException($"Outbox row {row.Id} has an unreadable payload for kind {row.Kind}.");

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];
}

/// <summary>
/// Spec §7.10's daily digest job - queues (never sends directly) the three warning stages, once per link
/// per stage (the link's own *_notice_queued_at), at most once per user per stage per day (the outbox's
/// partial unique index). Runs once a day; "once a day" here means once per process start per UTC day
/// rather than a precise cron at DailyRunUtc, since this build has no distributed scheduler - acceptable
/// for a single-instance year-1 deployment (see docs/TRACEABILITY.md).
/// </summary>
public sealed class EmailDigestJob(IServiceScopeFactory scopes, IOptions<EmailOptions> options, TimeProvider clock, ILogger<EmailDigestJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var lastRunDate = DateOnly.MinValue;
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                var now = clock.GetUtcNow();
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                var dueAt = today.ToDateTime(options.Value.DailyRunUtc, DateTimeKind.Utc);
                if (today > lastRunDate && now.UtcDateTime >= dueAt)
                {
                    await RunOnceAsync(today, stoppingToken);
                    lastRunDate = today;
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async Task RunOnceAsync(DateOnly today, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var links = scope.ServiceProvider.GetRequiredService<ILinkDigestSource>();
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var stage in new[] { DigestStages.Expiring, DigestStages.Expired, DigestStages.Purge })
        {
            var candidates = await links.GetCandidatesAsync(stage, now, ct);
            if (candidates.Count == 0) continue;

            var queuedLinkIds = new List<Guid>();
            foreach (var group in candidates.GroupBy(c => c.OwnerId))
            {
                // One transaction per user and stage (spec §7.10): the outbox row, its items, and the
                // per-stage dedup all commit together, or none do.
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var outbox = new EmailOutbox
                {
                    Kind = stage,
                    UserId = group.Key,
                    DigestDate = today,
                    Items = group.Select(c => new EmailOutboxItem { LinkId = c.LinkId, Slug = c.Slug, ExpiresAtSnapshot = c.ExpiresAt }).ToList(),
                };
                db.EmailOutbox.Add(outbox);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Partial unique index hit: another instance already queued this user+stage+day.
                    await tx.RollbackAsync(ct);
                    continue;
                }
                await tx.CommitAsync(ct);
                queuedLinkIds.AddRange(group.Select(c => c.LinkId));
            }

            if (queuedLinkIds.Count > 0) await links.MarkQueuedAsync(stage, queuedLinkIds, now, ct);
            logger.LogInformation(
                "Digest job: queued {UserCount} {Stage} notices covering {LinkCount} links.",
                candidates.GroupBy(c => c.OwnerId).Count(), stage, candidates.Count);
        }
    }
}
