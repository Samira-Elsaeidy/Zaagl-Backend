using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Collaboration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VoiceBriefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "briefs",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    audio_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    timeline = table.Column<string>(type: "jsonb", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_briefs", x => x.resource_id);
                    table.CheckConstraint("ck_briefs_duration", "duration_ms BETWEEN 1 AND 300000");
                    table.CheckConstraint("ck_briefs_events", "event_count BETWEEN 0 AND 20000");
                    table.CheckConstraint("ck_briefs_parts", "subject_resource_id <> audio_resource_id");
                    table.CheckConstraint("ck_briefs_subject_kind", "subject_kind IN ('image', 'document')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_briefs_owner",
                schema: "collab",
                table: "briefs",
                columns: new[] { "owner_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "briefs",
                schema: "collab");
        }
    }
}
