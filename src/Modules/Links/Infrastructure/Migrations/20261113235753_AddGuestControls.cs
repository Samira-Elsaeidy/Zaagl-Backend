using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <summary>
    /// Guest-creation control panel (PHASE1_STATUS "CAPTCHA admin control panel"): links.guest_settings holds the run-time
    /// guest settings (one row; no row = configuration defaults) and links.guest_settings_audit the who/when/old/new of every
    /// change. Expand-only: two new tables, nothing existing is touched.
    /// </summary>
    public partial class AddGuestControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guest_settings",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    link_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    link_captcha = table.Column<bool>(type: "boolean", nullable: false),
                    upload_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    upload_captcha = table.Column<bool>(type: "boolean", nullable: false),
                    snippet_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    snippet_captcha = table.Column<bool>(type: "boolean", nullable: false),
                    per_minute = table.Column<int>(type: "integer", nullable: false),
                    global_per_hour = table.Column<int>(type: "integer", nullable: false),
                    content_per_hour = table.Column<int>(type: "integer", nullable: false),
                    content_per_day = table.Column<int>(type: "integer", nullable: false),
                    links_per_day = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_settings", x => x.id);
                    table.CheckConstraint("ck_guest_settings_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "guest_settings_audit",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_settings_audit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guest_settings_audit_at",
                schema: "links",
                table: "guest_settings_audit",
                column: "at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_settings",
                schema: "links");

            migrationBuilder.DropTable(
                name: "guest_settings_audit",
                schema: "links");
        }
    }
}
