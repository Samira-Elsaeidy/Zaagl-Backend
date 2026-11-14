using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <summary>Phase 2 (docs/phase2/viewers-3d-lottie-audio.md): the browser viewer validated by the scan task.</summary>
    public partial class AddViewerKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "viewer",
                schema: "files",
                table: "storage_objects",
                type: "smallint",
                nullable: true);

            // Expand-only: a nullable column (no rewrite) and its check added NOT VALID; ValidateViewerKind validates it on its own.
            migrationBuilder.Sql("ALTER TABLE files.storage_objects ADD CONSTRAINT ck_storage_objects_viewer CHECK (viewer IS NULL OR viewer IN (1, 2, 3)) NOT VALID;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_storage_objects_viewer",
                schema: "files",
                table: "storage_objects");

            migrationBuilder.DropColumn(
                name: "viewer",
                schema: "files",
                table: "storage_objects");
        }
    }
}
