using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Analytics.Infrastructure.Migrations
{
    /// <summary>
    /// Fixes partition bounds on PostgreSQL servers whose session TimeZone is not UTC.
    /// AddDailyStatsArchiveSettings declared <c>month_start</c> as <c>date</c>, and
    /// <c>date AT TIME ZONE 'UTC'</c> first casts the date to timestamptz in the session time zone, so
    /// on an EET server every bound moved by hours and new partitions overlapped existing ones
    /// (42P17). Working on a <c>timestamp</c> makes <c>AT TIME ZONE 'UTC'</c> mean "this wall time in
    /// UTC", which is what the Initial migration did. Function body otherwise unchanged.
    /// </summary>
    public partial class FixClickPartitionBoundsUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION analytics.ensure_click_partitions(p_today date, months_ahead integer)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, analytics, pg_temp
                SET lock_timeout = '2s'
                AS $$
                DECLARE
                    i integer;
                    month_start timestamp;
                    start_ts timestamp with time zone;
                    end_ts timestamp with time zone;
                    part_name text;
                    test_clock_ok boolean;
                BEGIN
                    IF months_ahead < 1 OR months_ahead > 12 THEN
                        RAISE EXCEPTION 'months_ahead must be between 1 and 12, got %', months_ahead;
                    END IF;

                    SELECT (value = 'true') INTO test_clock_ok FROM analytics.settings WHERE key = 'test_clock_allowed';
                    IF NOT COALESCE(test_clock_ok, false) AND abs(p_today - current_date) > 2 THEN
                        RAISE EXCEPTION 'p_today (%) is more than 2 days from the database clock (%); set test_clock_allowed to use a fake clock in tests',
                            p_today, current_date;
                    END IF;

                    FOR i IN 0..months_ahead LOOP
                        month_start := date_trunc('month', p_today::timestamp) + make_interval(months => i);
                        start_ts := month_start AT TIME ZONE 'UTC';
                        end_ts := (month_start + interval '1 month') AT TIME ZONE 'UTC';
                        part_name := 'click_events_' || to_char(month_start, 'YYYY_MM');

                        IF to_regclass('analytics.' || part_name) IS NULL THEN
                            LOCK TABLE analytics.click_events_default IN ACCESS EXCLUSIVE MODE;
                            EXECUTE format(
                                'CREATE TABLE analytics.%I (LIKE analytics.click_events INCLUDING DEFAULTS INCLUDING CONSTRAINTS)',
                                part_name);
                            EXECUTE format(
                                'WITH moved AS (DELETE FROM analytics.click_events_default
                                     WHERE occurred_at >= %L AND occurred_at < %L RETURNING *)
                                 INSERT INTO analytics.%I SELECT * FROM moved',
                                start_ts, end_ts, part_name);
                            EXECUTE format(
                                'ALTER TABLE analytics.click_events ATTACH PARTITION analytics.%I FOR VALUES FROM (%L) TO (%L)',
                                part_name, start_ts, end_ts);
                        END IF;
                    END LOOP;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Migrations are expand-only (spec §3); a rollback redeploys the old image without running Down,
            // and the previous body is the buggy one, so there is nothing worth restoring here.
        }
    }
}
