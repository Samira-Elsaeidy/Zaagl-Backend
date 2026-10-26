using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Analytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyStatsArchiveSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_archive",
                schema: "analytics",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_custom = table.Column<bool>(type: "boolean", nullable: false),
                    target_url = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    purged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_archive", x => x.link_id);
                });

            migrationBuilder.CreateTable(
                name: "link_daily_stats",
                schema: "analytics",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    clicks = table.Column<long>(type: "bigint", nullable: false),
                    bot_clicks = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_daily_stats", x => new { x.link_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "analytics",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_link_archive_owner_id_purged_at",
                schema: "analytics",
                table: "link_archive",
                columns: new[] { "owner_id", "purged_at" });

            migrationBuilder.Sql("""
                INSERT INTO analytics.settings (key, value) VALUES
                    ('click_retention_months', '6'),
                    ('test_clock_allowed', 'false')
                ON CONFLICT (key) DO NOTHING;
                """);

            // Spec §4.4: both functions take p_today from the application's clock, never SQL now(), so a
            // fake clock in tests can drive them; both reject a p_today more than 2 days from the real
            // database date unless test_clock_allowed is set (test databases only).
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.ensure_click_partitions(integer);");

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
                    month_start date;
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
                        month_start := date_trunc('month', p_today) + make_interval(months => i);
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

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION analytics.drop_expired_click_partitions(p_today date)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, analytics, pg_temp
                SET lock_timeout = '2s'
                AS $$
                DECLARE
                    retention_months integer;
                    test_clock_ok boolean;
                    cutoff date;
                    part record;
                    part_month date;
                BEGIN
                    SELECT (value = 'true') INTO test_clock_ok FROM analytics.settings WHERE key = 'test_clock_allowed';
                    IF NOT COALESCE(test_clock_ok, false) AND abs(p_today - current_date) > 2 THEN
                        RAISE EXCEPTION 'p_today (%) is more than 2 days from the database clock (%); set test_clock_allowed to use a fake clock in tests',
                            p_today, current_date;
                    END IF;

                    SELECT value::integer INTO retention_months FROM analytics.settings WHERE key = 'click_retention_months';
                    retention_months := COALESCE(retention_months, 6);
                    cutoff := date_trunc('month', p_today) - make_interval(months => retention_months);

                    FOR part IN
                        SELECT c.relname AS name
                        FROM pg_inherits i
                        JOIN pg_class c ON c.oid = i.inhrelid
                        JOIN pg_class p ON p.oid = i.inhparent
                        WHERE p.relname = 'click_events' AND c.relname <> 'click_events_default'
                    LOOP
                        BEGIN
                            part_month := to_date(substring(part.name from 'click_events_(\d{4}_\d{2})'), 'YYYY_MM');
                        EXCEPTION WHEN OTHERS THEN
                            CONTINUE; -- not a month-named partition; leave it alone
                        END;
                        IF part_month + interval '1 month' <= cutoff THEN
                            EXECUTE format('ALTER TABLE analytics.click_events DETACH PARTITION analytics.%I', part.name);
                            EXECUTE format('DROP TABLE analytics.%I', part.name);
                        END IF;
                    END LOOP;

                    DELETE FROM analytics.click_events_default WHERE occurred_at < cutoff;
                END;
                $$;
                """);

            migrationBuilder.Sql("REVOKE EXECUTE ON FUNCTION analytics.ensure_click_partitions(date, integer) FROM PUBLIC;");
            migrationBuilder.Sql("REVOKE EXECUTE ON FUNCTION analytics.drop_expired_click_partitions(date) FROM PUBLIC;");

            migrationBuilder.Sql("SELECT analytics.ensure_click_partitions(current_date, 3);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.drop_expired_click_partitions(date);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.ensure_click_partitions(date, integer);");

            migrationBuilder.DropTable(
                name: "link_archive",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "link_daily_stats",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "analytics");
        }
    }
}
