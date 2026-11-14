using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 (docs/phase2): validates <c>ck_storage_objects_viewer</c>, added NOT VALID by the previous migration. Its own migration, outside any transaction
    /// (suppressTransaction), like ValidateLinkTargetKindChecks: VALIDATE takes only SHARE UPDATE EXCLUSIVE (reads and writes continue), and
    /// re-running it on a valid constraint is a no-op.
    /// </summary>
    public partial class ValidateViewerKind : Migration
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
