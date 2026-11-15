using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.Modules.Links.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Links.Application;

/// <summary>
/// The guest settings every guest create route reads at request time (no restart). Cached in memory for
/// <see cref="CacheTtl"/> and dropped at once on this instance when an admin saves; other instances pick a change up when
/// their copy expires. A failed read keeps serving the last value it read (never the configuration defaults, which could
/// silently undo an admin's pause); with no value at all it returns null and the caller refuses the guest with a 503.
/// </summary>
public sealed class GuestSettingsStore(
    IServiceScopeFactory scopes, IOptions<GuestOptions> options, TimeProvider time, ILogger<GuestSettingsStore> logger)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private sealed record Snapshot(GuestSettings Settings, DateTimeOffset ReadAt);

    private volatile Snapshot? _snapshot;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    /// <summary>The configuration values (<c>Guests:*</c>): every feature on, CAPTCHA required on every feature.</summary>
    public GuestSettings Defaults
    {
        get
        {
            var o = options.Value;
            var on = new GuestFeatureSettings(Enabled: true, CaptchaRequired: true);
            return new GuestSettings(on, on, on,
                new GuestLimitSettings(o.PerMinute, o.GlobalPerHour, o.ContentPerHour, o.ContentPerDay, o.LinksPerDay),
                Version: 0, UpdatedAt: null, UpdatedBy: null, Drop: on, Table: on);
        }
    }

    public async Task<GuestSettings?> GetAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && time.GetUtcNow() - current.ReadAt < CacheTtl) return current.Settings;

        await _refresh.WaitAsync(ct);
        try
        {
            current = _snapshot;
            if (current is not null && time.GetUtcNow() - current.ReadAt < CacheTtl) return current.Settings;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LinksDbContext>();
                var settings = await ReadAsync(db, ct);
                _snapshot = new Snapshot(settings, time.GetUtcNow());
                return settings;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (current is not null)
                {
                    logger.LogError(ex, "Reading the guest settings failed; serving the last value read at {ReadAt}", current.ReadAt);
                    return current.Settings;
                }
                logger.LogError(ex, "Reading the guest settings failed and no earlier value is known; guest creation is refused");
                return null;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Drops this instance's cached copy (after a save on this instance).</summary>
    public void Invalidate() => _snapshot = null;

    public async Task<GuestSettings> ReadAsync(LinksDbContext db, CancellationToken ct)
    {
        var row = await db.GuestSettings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, ct);
        return row is null ? Defaults : ToSettings(row);
    }

    public static GuestSettings ToSettings(GuestSettingsRow r) => new(
        new GuestFeatureSettings(r.LinkEnabled, r.LinkCaptcha),
        new GuestFeatureSettings(r.UploadEnabled, r.UploadCaptcha),
        new GuestFeatureSettings(r.SnippetEnabled, r.SnippetCaptcha),
        new GuestLimitSettings(r.PerMinute, r.GlobalPerHour, r.ContentPerHour, r.ContentPerDay, r.LinksPerDay),
        r.Version, DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc), r.UpdatedByEmail,
        new GuestFeatureSettings(r.DropEnabled, r.DropCaptcha),
        new GuestFeatureSettings(r.TableEnabled, r.TableCaptcha));
}

/// <summary>
/// In-memory, per-instance counters for the panel's stats: CAPTCHA refusals and paused refusals per guest feature since this
/// process started. Nothing about the visitor is kept (spec MUST NOT #26: no events or IP data for guests).
/// </summary>
public sealed class GuestControlCounters(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, long> _captchaFailures = new();
    private readonly ConcurrentDictionary<string, long> _pausedRefusals = new();

    public DateTime Since { get; } = time.GetUtcNow().UtcDateTime;

    public void CaptchaFailed(string feature) => _captchaFailures.AddOrUpdate(feature, 1, (_, n) => n + 1);
    public void PausedRefusal(string feature) => _pausedRefusals.AddOrUpdate(feature, 1, (_, n) => n + 1);

    public long CaptchaFailures(string feature) => _captchaFailures.GetValueOrDefault(feature);
    public long PausedRefusals(string feature) => _pausedRefusals.GetValueOrDefault(feature);
}

/// <summary>The admin side of the panel: validated saves, pause/resume all, the audit trail and the stats.</summary>
public sealed class GuestControlService(
    LinksDbContext db, GuestSettingsStore store, GuestControlCounters counters, TimeProvider time, ILogger<GuestControlService> logger)
{
    public static readonly GuestLimitBounds Bounds = new(
        PerMinute: new IntRange(1, 1_000),
        GlobalPerHour: new IntRange(1, 1_000_000),
        ContentPerHour: new IntRange(1, 10_000),
        ContentPerDay: new IntRange(1, 100_000),
        LinksPerDay: new IntRange(1, 10_000));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when valid; otherwise the first problem found.</summary>
    public static string? Validate(UpdateGuestSettingsRequest request)
    {
        if (request.Link is null || request.Upload is null || request.Snippet is null)
            return "link, upload and snippet are required.";
        if (request.Limits is not { } l) return "limits is required.";
        if (request.Reason is { Length: > 500 }) return "reason must be at most 500 characters.";

        static string? Check(string name, int value, IntRange range) =>
            value < range.Min || value > range.Max ? $"limits.{name} must be between {range.Min} and {range.Max}." : null;

        return Check("perMinute", l.PerMinute, Bounds.PerMinute)
            ?? Check("globalPerHour", l.GlobalPerHour, Bounds.GlobalPerHour)
            ?? Check("contentPerHour", l.ContentPerHour, Bounds.ContentPerHour)
            ?? Check("contentPerDay", l.ContentPerDay, Bounds.ContentPerDay)
            ?? Check("linksPerDay", l.LinksPerDay, Bounds.LinksPerDay)
            ?? (l.ContentPerDay < l.ContentPerHour ? "limits.contentPerDay must be at least limits.contentPerHour." : null);
    }

    public Task<Result<GuestSettings>> UpdateAsync(UpdateGuestSettingsRequest request, Guid actorId, string? actorEmail, CancellationToken ct)
    {
        var problem = Validate(request);
        if (problem is not null) return Task.FromResult<Result<GuestSettings>>(Error.Validation(problem));
        return SaveAsync(GuestAuditActions.Update, actorId, actorEmail, request.Reason, request.Version,
            s => new GuestSettings(request.Link!, request.Upload!, request.Snippet!, request.Limits!, 0, null, null,
                request.Drop ?? s.DropOrDefault, request.Table ?? s.TableOrDefault), ct);
    }

    /// <summary>Turns every guest feature off (CAPTCHA and limits unchanged).</summary>
    public Task<Result<GuestSettings>> PauseAllAsync(Guid actorId, string? actorEmail, string? reason, CancellationToken ct) =>
        SaveAsync(GuestAuditActions.PauseAll, actorId, actorEmail, reason, expectedVersion: null,
            s => s with
            {
                Link = s.Link with { Enabled = false }, Upload = s.Upload with { Enabled = false }, Snippet = s.Snippet with { Enabled = false },
                Drop = s.DropOrDefault with { Enabled = false }, Table = s.TableOrDefault with { Enabled = false },
            }, ct);

    /// <summary>Turns every guest feature back on (CAPTCHA and limits unchanged).</summary>
    public Task<Result<GuestSettings>> ResumeAllAsync(Guid actorId, string? actorEmail, string? reason, CancellationToken ct) =>
        SaveAsync(GuestAuditActions.ResumeAll, actorId, actorEmail, reason, expectedVersion: null,
            s => s with
            {
                Link = s.Link with { Enabled = true }, Upload = s.Upload with { Enabled = true }, Snippet = s.Snippet with { Enabled = true },
                Drop = s.DropOrDefault with { Enabled = true }, Table = s.TableOrDefault with { Enabled = true },
            }, ct);

    private async Task<Result<GuestSettings>> SaveAsync(
        string action, Guid actorId, string? actorEmail, string? reason, int? expectedVersion,
        Func<GuestSettings, GuestSettings> change, CancellationToken ct)
    {
        if (reason is { Length: > 500 }) return Error.Validation("reason must be at most 500 characters.");
        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Serialise saves: lock the row, or (first save ever) a transaction-scoped advisory lock so two first saves
        // cannot both insert.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('zaagl.guest_settings', 0))", ct);
        var row = await db.GuestSettings.FirstOrDefaultAsync(r => r.Id == 1, ct);
        var before = row is null ? store.Defaults : GuestSettingsStore.ToSettings(row);
        if (expectedVersion is { } v && v != before.Version)
            return Error.Conflict("The guest settings were changed by someone else. Reload and try again.");

        var after = change(before);
        var changes = Diff(before, after);
        if (changes.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return before;
        }

        if (row is null)
        {
            row = new GuestSettingsRow { Id = 1 };
            db.GuestSettings.Add(row);
        }
        row.LinkEnabled = after.Link.Enabled;
        row.LinkCaptcha = after.Link.CaptchaRequired;
        row.UploadEnabled = after.Upload.Enabled;
        row.UploadCaptcha = after.Upload.CaptchaRequired;
        row.SnippetEnabled = after.Snippet.Enabled;
        row.SnippetCaptcha = after.Snippet.CaptchaRequired;
        row.DropEnabled = after.DropOrDefault.Enabled;
        row.DropCaptcha = after.DropOrDefault.CaptchaRequired;
        row.TableEnabled = after.TableOrDefault.Enabled;
        row.TableCaptcha = after.TableOrDefault.CaptchaRequired;
        row.PerMinute = after.Limits.PerMinute;
        row.GlobalPerHour = after.Limits.GlobalPerHour;
        row.ContentPerHour = after.Limits.ContentPerHour;
        row.ContentPerDay = after.Limits.ContentPerDay;
        row.LinksPerDay = after.Limits.LinksPerDay;
        row.Version = before.Version + 1;
        row.UpdatedAt = now;
        row.UpdatedBy = actorId;
        row.UpdatedByEmail = actorEmail;

        db.GuestSettingsAudit.Add(new GuestSettingsAuditRow
        {
            At = now,
            ActorId = actorId,
            ActorEmail = actorEmail,
            Action = action,
            Changes = JsonSerializer.Serialize(changes, Json),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        store.Invalidate();

        // Every change is logged loudly, and a CAPTCHA switched off is a warning of its own (it weakens abuse protection).
        logger.LogWarning("Guest settings {Action} by {AdminId} ({AdminEmail}), version {Version}: {Changes}",
            action, actorId, actorEmail, row.Version, string.Join("; ", changes.Select(c => $"{c.Field}: {c.Old} -> {c.New}")));
        foreach (var c in changes.Where(c => c.Field.EndsWith(".captchaRequired", StringComparison.Ordinal) && c.New == "false"))
            logger.LogWarning("CAPTCHA turned OFF for {Feature} by {AdminEmail}", c.Field[..c.Field.IndexOf('.')], actorEmail);

        return GuestSettingsStore.ToSettings(row);
    }

    private static List<GuestSettingChange> Diff(GuestSettings a, GuestSettings b)
    {
        var list = new List<GuestSettingChange>();
        void Add(string field, object x, object y)
        {
            var (ox, oy) = (Format(x), Format(y));
            if (ox != oy) list.Add(new GuestSettingChange(field, ox, oy));
        }
        static string Format(object v) => v is bool flag ? (flag ? "true" : "false") : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";

        foreach (var feature in GuestFeatures.All)
        {
            Add($"{feature}.enabled", a.For(feature).Enabled, b.For(feature).Enabled);
            Add($"{feature}.captchaRequired", a.For(feature).CaptchaRequired, b.For(feature).CaptchaRequired);
        }
        Add("limits.perMinute", a.Limits.PerMinute, b.Limits.PerMinute);
        Add("limits.globalPerHour", a.Limits.GlobalPerHour, b.Limits.GlobalPerHour);
        Add("limits.contentPerHour", a.Limits.ContentPerHour, b.Limits.ContentPerHour);
        Add("limits.contentPerDay", a.Limits.ContentPerDay, b.Limits.ContentPerDay);
        Add("limits.linksPerDay", a.Limits.LinksPerDay, b.Limits.LinksPerDay);
        return list;
    }

    public async Task<IReadOnlyList<GuestAuditEntryDto>> AuditAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        var rows = await db.GuestSettingsAudit.AsNoTracking().OrderByDescending(r => r.Id).Take(limit).ToListAsync(ct);
        return rows.Select(r => new GuestAuditEntryDto(
            r.Id, DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.ActorId, r.ActorEmail, r.Action,
            JsonSerializer.Deserialize<List<GuestSettingChange>>(r.Changes, Json) ?? [], r.Reason)).ToList();
    }

    public async Task<GuestStatsDto> StatsAsync(GuestSettings settings, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hour = now.AddHours(-1);
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);

        var grouped = await db.Links.AsNoTracking()
            .Where(l => l.GuestCreatorKey != null && l.CreatedAt > week)
            .GroupBy(l => l.TargetKind)
            .Select(g => new
            {
                Kind = g.Key,
                Hour = g.Count(l => l.CreatedAt > hour),
                Day = g.Count(l => l.CreatedAt > day),
                Week = g.Count(),
            })
            .ToListAsync(ct);

        GuestFeatureStats For(string feature, params LinkTargetKind[] kinds)
        {
            var rows = grouped.Where(g => kinds.Contains(g.Kind)).ToList();
            var f = settings.For(feature);
            return new GuestFeatureStats(feature, rows.Sum(r => r.Hour), rows.Sum(r => r.Day), rows.Sum(r => r.Week),
                f.Enabled, f.CaptchaRequired, counters.CaptchaFailures(feature), counters.PausedRefusals(feature));
        }

        return new GuestStatsDto(
            [
                For(GuestFeatures.Link, LinkTargetKind.Url),
                For(GuestFeatures.Upload, LinkTargetKind.File, LinkTargetKind.Media),
                For(GuestFeatures.Snippet, LinkTargetKind.Snippet),
                // Drop uploads land in the owner's library (owner rows, not guest rows): no creation counts, only the panel counters.
                For(GuestFeatures.Drop),
                For(GuestFeatures.Table, LinkTargetKind.Table),
            ],
            settings.AllPaused, counters.Since, now);
    }
}
