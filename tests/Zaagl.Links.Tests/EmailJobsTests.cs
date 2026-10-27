using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Identity;
using Zaagl.Modules.Identity.Application;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.Modules.Links;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>Spec §7.10, against the real local dev Postgres (same convention as the other integration
/// suites in this project).</summary>
public sealed class EmailJobsTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
    private readonly ServiceProvider _services;
    private readonly IdentityDbContext _db;

    public EmailJobsTests()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<IdentityDbContext>(o => IdentityModule.Configure(o, ConnectionString));
        _services = sc.BuildServiceProvider();
        _db = _services.GetRequiredService<IdentityDbContext>();
    }

    public void Dispose()
    {
        _db.Dispose();
        _services.Dispose();
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Exception? ThrowOnSend { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (ThrowOnSend is { } ex) throw ex;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private EmailDispatcher MakeDispatcher(IEmailSender sender, TimeProvider? time = null)
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<IdentityDbContext>(o => IdentityModule.Configure(o, ConnectionString));
        var sp = sc.BuildServiceProvider();
        return new EmailDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(), sender,
            Options.Create(new EmailOptions { ApiBaseUrl = "http://localhost:5084" }),
            time ?? TimeProvider.System, NullLogger<EmailDispatcher>.Instance);
    }

    private async Task<User> CreateConfirmedUserAsync()
    {
        var user = new User
        {
            Email = $"email-test-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x",
            DisplayName = "Test",
            EmailConfirmed = true,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Queued_confirm_email_is_sent_with_the_fragment_url()
    {
        var user = await CreateConfirmedUserAsync();
        await EmailQueue.QueueAccountEmailAsync(_db, user.Id, EmailKinds.ConfirmEmail,
            new { Token = "abc123" }, null, default);

        var sender = new RecordingEmailSender();
        await MakeDispatcher(sender).RunOnceAsync(default);

        // Filtered rather than Assert.Single(sender.Sent): other tests in this class share the same
        // Postgres instance and outbox table and may run concurrently (xUnit parallelises by class).
        var sent = Assert.Single(sender.Sent, m => m.ToEmail == user.Email);
        Assert.Contains($"u={user.Id}&t=abc123", sent.TextBody + sent.HtmlBody);

        var row = await _db.EmailOutbox.AsNoTracking().FirstAsync(e => e.UserId == user.Id && e.Kind == EmailKinds.ConfirmEmail);
        Assert.Equal(EmailOutboxStatuses.Sent, row.Status);
        Assert.NotNull(row.SentAt);
    }

    [Fact]
    public async Task Suppressed_address_is_cancelled_without_calling_the_sender()
    {
        var user = await CreateConfirmedUserAsync();
        _db.EmailSuppressions.Add(new EmailSuppression { Email = user.Email, Reason = "test" });
        await EmailQueue.QueueAccountEmailAsync(_db, user.Id, EmailKinds.ConfirmEmail, new { Token = "x" }, null, default);
        await _db.SaveChangesAsync();

        var sender = new RecordingEmailSender();
        await MakeDispatcher(sender).RunOnceAsync(default);

        Assert.DoesNotContain(sender.Sent, m => m.ToEmail == user.Email);
        var row = await _db.EmailOutbox.AsNoTracking().FirstAsync(e => e.UserId == user.Id && e.Kind == EmailKinds.ConfirmEmail);
        Assert.Equal(EmailOutboxStatuses.Cancelled, row.Status);
    }

    [Fact]
    public async Task Disabled_user_gets_no_warning_email_but_security_notice_still_goes_through()
    {
        var user = await CreateConfirmedUserAsync();
        user.DisabledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await EmailQueue.QueueAccountEmailAsync(_db, user.Id, EmailKinds.ConfirmEmail, new { Token = "x" }, null, default);
        await EmailQueue.QueueAccountEmailAsync(_db, user.Id, EmailKinds.SecurityNotice, new { Message = "notice" }, user.Email, default);

        var sender = new RecordingEmailSender();
        await MakeDispatcher(sender).RunOnceAsync(default);

        Assert.Single(sender.Sent, m => m.ToEmail == user.Email); // only the security-notice, filtered by recipient
        var confirmRow = await _db.EmailOutbox.AsNoTracking().FirstAsync(e => e.UserId == user.Id && e.Kind == EmailKinds.ConfirmEmail);
        Assert.Equal(EmailOutboxStatuses.Cancelled, confirmRow.Status);
        var noticeRow = await _db.EmailOutbox.AsNoTracking().FirstAsync(e => e.UserId == user.Id && e.Kind == EmailKinds.SecurityNotice);
        Assert.Equal(EmailOutboxStatuses.Sent, noticeRow.Status);
        Assert.Null(noticeRow.RecipientEmail); // cleared after send, spec §7.14
    }

    [Fact]
    public async Task Transient_failure_reschedules_with_backoff()
    {
        var user = await CreateConfirmedUserAsync();
        var beforeSend = DateTime.UtcNow;
        await EmailQueue.QueueAccountEmailAsync(_db, user.Id, EmailKinds.ConfirmEmail, new { Token = "x" }, null, default);

        var sender = new RecordingEmailSender { ThrowOnSend = new InvalidOperationException("smtp down") };
        await MakeDispatcher(sender).RunOnceAsync(default); // real-time TimeProvider - no frozen-clock precision trap

        var row = await _db.EmailOutbox.AsNoTracking().FirstAsync(e => e.UserId == user.Id && e.Kind == EmailKinds.ConfirmEmail);
        Assert.Equal(EmailOutboxStatuses.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.True(row.SendAfter > beforeSend.AddSeconds(30)); // first backoff step is 1 minute
    }

    [Fact]
    public async Task Permanent_failure_marks_failed_and_suppresses_the_address()
    {
        var user = await CreateConfirmedUserAsync();
        await EmailQueue.QueueAccountEmailAsync(_db, user.Id, EmailKinds.ConfirmEmail, new { Token = "y" }, null, default);

        var permanentSender = new RecordingEmailSender { ThrowOnSend = new EmailPermanentFailureException("bounced") };
        await MakeDispatcher(permanentSender).RunOnceAsync(default);

        var row = await _db.EmailOutbox.AsNoTracking().FirstAsync(e => e.UserId == user.Id && e.Kind == EmailKinds.ConfirmEmail);
        Assert.Equal(EmailOutboxStatuses.Failed, row.Status);
        Assert.True(await _db.EmailSuppressions.AnyAsync(s => s.Email == user.Email));
    }

    [Fact]
    public async Task Digest_job_queues_at_most_one_per_stage_per_user_per_day()
    {
        var user = await CreateConfirmedUserAsync();
        var items = new List<DigestCandidate>
        {
            new(Guid.CreateVersion7(), user.Id, "linkA", DateTime.UtcNow.AddDays(3)),
            new(Guid.CreateVersion7(), user.Id, "linkB", DateTime.UtcNow.AddDays(5)),
        };

        var sc = new ServiceCollection();
        sc.AddDbContext<IdentityDbContext>(o => IdentityModule.Configure(o, ConnectionString));
        sc.AddSingleton<ILinkDigestSource>(new FakeDigestSource(items));
        var sp = sc.BuildServiceProvider();
        var job = new EmailDigestJob(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new EmailOptions()), TimeProvider.System, NullLogger<EmailDigestJob>.Instance);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await job.RunOnceAsync(today, default);

        var rows = await _db.EmailOutbox.Include(e => e.Items).Where(e => e.UserId == user.Id && e.Kind == EmailKinds.Expiring).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(2, rows[0].Items.Count);

        // Running again the same day for the same stage/user must not duplicate (partial unique index).
        await job.RunOnceAsync(today, default);
        var rowsAfter = await _db.EmailOutbox.Where(e => e.UserId == user.Id && e.Kind == EmailKinds.Expiring).ToListAsync();
        Assert.Single(rowsAfter);
    }

    private sealed class FakeDigestSource(IReadOnlyList<DigestCandidate> candidates) : ILinkDigestSource
    {
        public Task<IReadOnlyList<DigestCandidate>> GetCandidatesAsync(string stage, DateTime now, CancellationToken ct) =>
            Task.FromResult(stage == DigestStages.Expiring ? candidates : (IReadOnlyList<DigestCandidate>)[]);

        public Task MarkQueuedAsync(string stage, IReadOnlyList<Guid> linkIds, DateTime now, CancellationToken ct) => Task.CompletedTask;
    }
}
