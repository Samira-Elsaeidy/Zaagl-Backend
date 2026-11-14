using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 (docs/phase2/bug-capsules.md): widens <c>ck_storage_objects_viewer</c> to the video viewer (4). Expand-only: existing rows
    /// already satisfy it. Dropped and re-added NOT VALID in one transaction; ValidateViewerKindForVideo validates it on its own.
    /// </summary>
    public partial class WidenViewerKindForVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE files.storage_objects DROP CONSTRAINT IF EXISTS ck_storage_objects_viewer;
                ALTER TABLE files.storage_objects ADD CONSTRAINT ck_storage_objects_viewer CHECK (viewer IS NULL OR viewer IN (1, 2, 3, 4)) NOT VALID;
                """);
        }

        /// <summary>Local development only.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE files.storage_objects DROP CONSTRAINT IF EXISTS ck_storage_objects_viewer;
                ALTER TABLE files.storage_objects ADD CONSTRAINT ck_storage_objects_viewer CHECK (viewer IS NULL OR viewer IN (1, 2, 3)) NOT VALID;
                """);
        }
    }
}
