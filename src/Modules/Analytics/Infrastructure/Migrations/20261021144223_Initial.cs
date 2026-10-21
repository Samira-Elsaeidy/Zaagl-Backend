using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Analytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <summary>
        /// Hand-edited: EF cannot express PARTITION BY RANGE, so click_events is created with SQL.
        /// The column layout still matches the model snapshot exactly.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.Sql("""
                CREATE TABLE analytics.click_events (
                    id            uuid                     NOT NULL,
                    occurred_at   timestamp with time zone NOT NULL,
                    link_id       uuid                     NOT NULL,
                    visitor_hash  character varying(64)    NOT NULL,
                    referrer_host character varying(255)   NULL,
                    country       character varying(2)     NULL,
                    city          character varying(100)   NULL,
                    os            character varying(100)   NULL,
                    browser       character varying(100)   NULL,
                    device_type   character varying(20)    NOT NULL,
                    is_bot        boolean                  NOT NULL,
                    CONSTRAINT pk_click_events PRIMARY KEY (id, occurred_at)
                ) PARTITION BY RANGE (occurred_at);
                """);

            // Catches anything outside the pre-created monthly partitions, so inserts never fail.
            migrationBuilder.Sql("""
                CREATE TABLE analytics.click_events_default PARTITION OF analytics.click_events DEFAULT;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_click_events_link_id_occurred_at
                    ON analytics.click_events (link_id, occurred_at);
                """);

            // Creates the current month plus the next `months` monthly partitions (UTC boundaries).
            // Safe to call repeatedly; the API calls it every 12 hours.
            migrationBuilder.Sql("""
                CREATE FUNCTION analytics.ensure_click_partitions(months integer) RETURNS void
                LANGUAGE plpgsql AS $$
                DECLARE
                    i integer;
                    month_start timestamp;
                    start_ts timestamp with time zone;
                    end_ts timestamp with time zone;
                    part_name text;
                BEGIN
                    FOR i IN 0..months LOOP
                        month_start := date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => i);
                        start_ts := month_start AT TIME ZONE 'UTC';
                        end_ts := (month_start + interval '1 month') AT TIME ZONE 'UTC';
                        part_name := 'click_events_' || to_char(month_start, 'YYYY_MM');

                        IF to_regclass('analytics.' || part_name) IS NULL THEN
                            EXECUTE format(
                                'CREATE TABLE analytics.%I PARTITION OF analytics.click_events FOR VALUES FROM (%L) TO (%L)',
                                part_name, start_ts, end_ts);
                        END IF;
                    END LOOP;
                END;
                $$;
                """);

            migrationBuilder.Sql("SELECT analytics.ensure_click_partitions(3);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.ensure_click_partitions(integer);");
            migrationBuilder.Sql("DROP TABLE IF EXISTS analytics.click_events;"); // drops its partitions too
        }
    }
}
