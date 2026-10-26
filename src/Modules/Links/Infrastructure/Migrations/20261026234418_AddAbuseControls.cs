using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAbuseControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "safety_checked_at",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_host",
                schema: "links",
                table: "links",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "abuse_evidence",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    disabled_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    keep_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_abuse_evidence", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "blocked_aliases",
                schema: "links",
                columns: table => new
                {
                    slug_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocked_aliases", x => x.slug_key);
                });

            migrationBuilder.CreateTable(
                name: "blocked_destinations",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pattern = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocked_destinations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_links_safety_checked_at",
                schema: "links",
                table: "links",
                column: "safety_checked_at");

            migrationBuilder.CreateIndex(
                name: "ix_links_target_host",
                schema: "links",
                table: "links",
                column: "target_host");

            migrationBuilder.CreateIndex(
                name: "ix_abuse_evidence_keep_until",
                schema: "links",
                table: "abuse_evidence",
                column: "keep_until");

            migrationBuilder.CreateIndex(
                name: "ix_abuse_evidence_link_id",
                schema: "links",
                table: "abuse_evidence",
                column: "link_id");

            migrationBuilder.CreateIndex(
                name: "ix_blocked_destinations_kind_pattern",
                schema: "links",
                table: "blocked_destinations",
                columns: new[] { "kind", "pattern" },
                unique: true);

            // Spec §7.15: one row per month, incremented atomically so N instances share one budget.
            // Not an EF-mapped entity - WebRiskChecker only ever reaches it through raw SQL.
            migrationBuilder.Sql("""
                CREATE TABLE links.safety_budget (
                    month date PRIMARY KEY,
                    used integer NOT NULL DEFAULT 0
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE links.safety_budget;");

            migrationBuilder.DropTable(
                name: "abuse_evidence",
                schema: "links");

            migrationBuilder.DropTable(
                name: "blocked_aliases",
                schema: "links");

            migrationBuilder.DropTable(
                name: "blocked_destinations",
                schema: "links");

            migrationBuilder.DropIndex(
                name: "ix_links_safety_checked_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropIndex(
                name: "ix_links_target_host",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "safety_checked_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "target_host",
                schema: "links",
                table: "links");
        }
    }
}
