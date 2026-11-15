using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 E2.3: validates <c>ck_upload_sessions_drop</c> (SHARE UPDATE EXCLUSIVE) and creates the per-portal index CONCURRENTLY, both
    /// outside any transaction (reads and writes continue). Idempotent.
    /// </summary>
    public partial class ValidateDropUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE files.upload_sessions VALIDATE CONSTRAINT ck_upload_sessions_drop;", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_upload_sessions_drop_portal ON files.upload_sessions (drop_portal_id, created_at) WHERE drop_portal_id IS NOT NULL;",
                suppressTransaction: true);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS files.ix_upload_sessions_drop_portal;");
        }
    }
}
