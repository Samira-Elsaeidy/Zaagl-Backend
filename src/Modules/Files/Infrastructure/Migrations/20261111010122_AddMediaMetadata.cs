using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_derivatives",
                schema: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    area = table.Column<short>(type: "smallint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    mime = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: true),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_derivatives", x => x.id);
                    table.CheckConstraint("ck_media_derivatives_status", "status IN (0, 1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "media_items",
                schema: "files",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_type = table.Column<short>(type: "smallint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    orientation = table.Column<short>(type: "smallint", nullable: true),
                    frame_count = table.Column<int>(type: "integer", nullable: true),
                    has_alpha = table.Column<bool>(type: "boolean", nullable: true),
                    probed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_items", x => x.resource_id);
                    table.CheckConstraint("ck_media_items_orientation", "orientation IS NULL OR orientation BETWEEN 1 AND 8");
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_derivatives_resource_id_kind",
                schema: "files",
                table: "media_derivatives",
                columns: new[] { "resource_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_derivatives",
                schema: "files");

            migrationBuilder.DropTable(
                name: "media_items",
                schema: "files");
        }
    }
}
