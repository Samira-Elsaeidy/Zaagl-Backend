using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Collaboration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropPortals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drop_files",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    portal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploader_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    uploader_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drop_files", x => x.resource_id);
                    table.CheckConstraint("ck_drop_files_email", "uploader_email IS NULL OR char_length(uploader_email) <= 254");
                    table.CheckConstraint("ck_drop_files_name", "uploader_name IS NULL OR char_length(uploader_name) <= 100");
                    table.CheckConstraint("ck_drop_files_note", "note IS NULL OR char_length(note) <= 1000");
                });

            migrationBuilder.CreateTable(
                name: "drop_portals",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    max_files = table.Column<int>(type: "integer", nullable: false),
                    max_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_file_bytes = table.Column<long>(type: "bigint", nullable: false),
                    allowed_types = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    require_name = table.Column<bool>(type: "boolean", nullable: false),
                    require_email = table.Column<bool>(type: "boolean", nullable: false),
                    notify_owner = table.Column<bool>(type: "boolean", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_notified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drop_portals", x => x.resource_id);
                    table.CheckConstraint("ck_drop_portals_caps", "max_files BETWEEN 1 AND 10000 AND max_bytes >= 1 AND max_file_bytes >= 1");
                    table.CheckConstraint("ck_drop_portals_instructions", "instructions IS NULL OR char_length(instructions) <= 2000");
                    table.CheckConstraint("ck_drop_portals_types", "char_length(allowed_types) <= 100");
                });

            migrationBuilder.CreateIndex(
                name: "ix_drop_files_portal",
                schema: "collab",
                table: "drop_files",
                columns: new[] { "portal_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_drop_files_session",
                schema: "collab",
                table: "drop_files",
                column: "upload_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_drop_portals_owner",
                schema: "collab",
                table: "drop_portals",
                columns: new[] { "owner_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drop_files",
                schema: "collab");

            migrationBuilder.DropTable(
                name: "drop_portals",
                schema: "collab");
        }
    }
}
