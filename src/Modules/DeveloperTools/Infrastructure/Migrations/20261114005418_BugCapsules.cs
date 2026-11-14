using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.DeveloperTools.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BugCapsules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capsules",
                schema: "devtools",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recording_resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    logs = table.Column<string>(type: "jsonb", nullable: false),
                    logs_bytes = table.Column<int>(type: "integer", nullable: false),
                    console_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    network_count = table.Column<int>(type: "integer", nullable: false),
                    failed_request_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capsules", x => x.resource_id);
                    table.CheckConstraint("ck_capsules_counts", "console_count >= 0 AND error_count >= 0 AND network_count >= 0 AND failed_request_count >= 0");
                    table.CheckConstraint("ck_capsules_logs_bytes", "logs_bytes BETWEEN 0 AND 4194304");
                });

            migrationBuilder.CreateIndex(
                name: "ix_capsules_recording",
                schema: "devtools",
                table: "capsules",
                column: "recording_resource_id",
                filter: "recording_resource_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capsules",
                schema: "devtools");
        }
    }
}
