using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 E2.3 (docs/phase2/drop-portals.md): an upload session may belong to a drop-off portal (<c>drop_portal_id</c>) and be reachable
    /// only with the uploader's token (<c>drop_token_hash</c>, SHA-256). Expand-only: two nullable columns (no rewrite) and a check added
    /// NOT VALID; ValidateDropUploads validates it and builds the portal index concurrently, outside any transaction.
    /// </summary>
    public partial class AddDropUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "drop_portal_id",
                schema: "files",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "drop_token_hash",
                schema: "files",
                table: "upload_sessions",
                type: "bytea",
                nullable: true);

            // NOT VALID: no scan of existing rows under ACCESS EXCLUSIVE (every existing row has both columns null anyway).
            migrationBuilder.Sql(
                """
                ALTER TABLE files.upload_sessions ADD CONSTRAINT ck_upload_sessions_drop
                    CHECK ((drop_portal_id IS NULL) = (drop_token_hash IS NULL) AND (drop_portal_id IS NULL OR owner_id IS NOT NULL)) NOT VALID;
                """);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS files.ix_upload_sessions_drop_portal;");
            migrationBuilder.Sql("ALTER TABLE files.upload_sessions DROP CONSTRAINT IF EXISTS ck_upload_sessions_drop;");
            migrationBuilder.DropColumn(name: "drop_portal_id", schema: "files", table: "upload_sessions");
            migrationBuilder.DropColumn(name: "drop_token_hash", schema: "files", table: "upload_sessions");
        }
    }
}
