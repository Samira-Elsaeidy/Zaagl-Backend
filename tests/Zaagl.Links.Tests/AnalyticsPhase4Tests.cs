using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zaagl.Modules.Analytics.Domain;
using Zaagl.Modules.Analytics.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>
/// Integration tests against the real local dev Postgres (same pattern as Zaagl.Coordinator.Tests -
/// no Testcontainers set up in this solution). Exercises the spec-exact partition functions and the
/// archive read path built in phase 4.
/// </summary>
public sealed class AnalyticsPhase4Tests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev";
    private readonly AnalyticsDbContext _db;

    public AnalyticsPhase4Tests()
    {
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(ConnectionString, o => o.MigrationsHistoryTable("__ef_migrations", "analytics"))
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new AnalyticsDbContext(options);
        _db.Database.Migrate();
        ResetTestClockSetting();
    }

    public void Dispose()
    {
        ResetTestClockSetting();
        _db.Dispose();
    }

    private void ResetTestClockSetting() =>
        _db.Database.ExecuteSqlRaw(
            "UPDATE analytics.settings SET value = 'false' WHERE key = 'test_clock_allowed'");

    [Fact]
    public void Ensure_click_partitions_rejects_a_skewed_clock_by_default()
    {
        var farFuture = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(14).Date);
        var ex = Assert.Throws<PostgresException>(() =>
            _db.Database.ExecuteSqlInterpolated($"SELECT analytics.ensure_click_partitions({farFuture}, 3)"));
        Assert.Contains("more than 2 days", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ensure_click_partitions_allows_a_skewed_clock_when_test_clock_allowed()
    {
        _db.Database.ExecuteSqlRaw("UPDATE analytics.settings SET value = 'true' WHERE key = 'test_clock_allowed'");
        var farFuture = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(14).Date);

        // Must not throw, and must actually create the far-future month's partition.
        _db.Database.ExecuteSqlInterpolated($"SELECT analytics.ensure_click_partitions({farFuture}, 1)");

        var partName = "click_events_" + farFuture.ToString("yyyy_MM");
        var exists = _db.Database.SqlQuery<bool>(
            $"SELECT (to_regclass('analytics.' || {partName}) IS NOT NULL) AS \"Value\"").Single();
        Assert.True(exists);
    }

    [Fact]
    public void Ensure_click_partitions_rejects_months_ahead_outside_1_to_12()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        Assert.Throws<PostgresException>(() =>
            _db.Database.ExecuteSqlInterpolated($"SELECT analytics.ensure_click_partitions({today}, 13)"));
        Assert.Throws<PostgresException>(() =>
            _db.Database.ExecuteSqlInterpolated($"SELECT analytics.ensure_click_partitions({today}, 0)"));
    }

    [Fact]
    public async Task Archive_queries_return_a_manually_inserted_row()
    {
        var linkId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _db.LinkArchive.Add(new LinkArchiveRow
        {
            LinkId = linkId,
            OwnerId = ownerId,
            Code = "abc12",
            IsCustom = false,
            TargetUrl = "https://example.com/archived",
            CreatedAt = now.AddDays(-100),
            ExpiresAt = now.AddDays(-10),
            PurgedAt = now.AddDays(-1),
        });
        _db.LinkDailyStats.Add(new LinkDailyStatRow { LinkId = linkId, Day = DateOnly.FromDateTime(now.AddDays(-50)), Clicks = 7, BotClicks = 1 });
        await _db.SaveChangesAsync();

        var queries = new ArchiveQueries(_db);
        var page = await queries.ListAsync(ownerId, 50, null, CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal("abc12", page.Items[0].Code);

        var found = await queries.FindAsync(ownerId, linkId, CancellationToken.None);
        Assert.NotNull(found);

        var stats = await queries.GetStatsAsync(linkId, CancellationToken.None);
        Assert.Single(stats.Series);
        Assert.Equal(7, stats.Series[0].Clicks);

        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM analytics.link_archive WHERE link_id = {linkId}");
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM analytics.link_daily_stats WHERE link_id = {linkId}");
    }
}
