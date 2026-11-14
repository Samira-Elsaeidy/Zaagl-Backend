using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <summary>Phase 2: validates the widened <c>ck_storage_objects_viewer</c> outside any transaction (SHARE UPDATE EXCLUSIVE only).</summary>
    public partial class ValidateViewerKindForVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE files.storage_objects VALIDATE CONSTRAINT ck_storage_objects_viewer;", suppressTransaction: true);
        }

        /// <summary>Expand-only: nothing to undo.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
