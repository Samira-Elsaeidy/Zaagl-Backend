using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Snippets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "snippets");

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "snippets",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.scope, x.key });
                });

            migrationBuilder.CreateTable(
                name: "snippets",
                schema: "snippets",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    version_count = table.Column<int>(type: "integer", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snippets", x => x.resource_id);
                    table.CheckConstraint("ck_snippets_current_eq_count", "current_version = version_count");
                    table.CheckConstraint("ck_snippets_current_version", "current_version BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_snippets_total_bytes", "total_bytes >= 0");
                    table.CheckConstraint("ck_snippets_version_count", "version_count BETWEEN 1 AND 1000");
                });

            migrationBuilder.CreateTable(
                name: "snippet_versions",
                schema: "snippets",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    render_mode = table.Column<short>(type: "smallint", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quota_committed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_via = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snippet_versions", x => new { x.resource_id, x.version });
                    table.CheckConstraint("ck_snippet_versions_language", "language IS NULL OR language ~ '^[a-z0-9][a-z0-9+#._-]{0,63}$'");
                    table.CheckConstraint("ck_snippet_versions_line_count", "line_count >= 1");
                    table.CheckConstraint("ck_snippet_versions_render_mode", "render_mode IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_snippet_versions_sha256", "octet_length(sha256) = 32");
                    table.CheckConstraint("ck_snippet_versions_size", "size_bytes BETWEEN 1 AND 2097152");
                    table.CheckConstraint("ck_snippet_versions_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_snippet_versions_snippets_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "snippets",
                        principalTable: "snippets",
                        principalColumn: "resource_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                schema: "snippets",
                table: "idempotency_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_snippet_versions_uncommitted",
                schema: "snippets",
                table: "snippet_versions",
                column: "created_at",
                filter: "quota_committed = false");

            // ARC-010 §2: versions are immutable. An UPDATE may only touch quota_committed and reservation_id
            // (the quota commit of ARC-010 §5); any change to the text, language, mode, size or hash is refused.
            // DELETE stays allowed (purge, orphan sweep, cascade) and is not covered by the trigger.
            migrationBuilder.Sql(@"
CREATE FUNCTION snippets.forbid_version_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.resource_id = OLD.resource_id AND NEW.version = OLD.version AND NEW.content = OLD.content
     AND NEW.language IS NOT DISTINCT FROM OLD.language AND NEW.render_mode = OLD.render_mode
     AND NEW.size_bytes = OLD.size_bytes AND NEW.line_count = OLD.line_count AND NEW.sha256 = OLD.sha256
     AND NEW.created_via = OLD.created_via AND NEW.created_at = OLD.created_at THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'snippet versions are immutable' USING ERRCODE = 'P0001';
END $$;");
            migrationBuilder.Sql(@"
CREATE TRIGGER trg_snippet_versions_immutable BEFORE UPDATE ON snippets.snippet_versions
  FOR EACH ROW EXECUTE FUNCTION snippets.forbid_version_update();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_snippet_versions_immutable ON snippets.snippet_versions;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS snippets.forbid_version_update();");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "snippets");

            migrationBuilder.DropTable(
                name: "snippet_versions",
                schema: "snippets");

            migrationBuilder.DropTable(
                name: "snippets",
                schema: "snippets");
        }
    }
}
