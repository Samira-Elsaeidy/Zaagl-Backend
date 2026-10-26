using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkLifecycleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "code_length",
                schema: "links",
                table: "links",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "code_number",
                schema: "links",
                table: "links",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "disabled_at",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disabled_kind",
                schema: "links",
                table: "links",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disabled_reason",
                schema: "links",
                table: "links",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_custom",
                schema: "links",
                table: "links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<short>(
                name: "key_version",
                schema: "links",
                table: "links",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "purge_after",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "links",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.scope, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_links_code_number",
                schema: "links",
                table: "links",
                columns: new[] { "code_length", "code_number" },
                unique: true,
                filter: "\"code_number\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_links_purge_after",
                schema: "links",
                table: "links",
                column: "purge_after");

            // Pre-existing rows predate IsCustom/CodeLength tracking; this build has no way to recover
            // whether they were originally generated or custom, so treat them as custom (they already
            // have a real, working slug either way) rather than leave them violating the new check below.
            migrationBuilder.Sql("UPDATE links.links SET is_custom = true WHERE code_length IS NULL AND NOT is_custom");

            migrationBuilder.AddCheckConstraint(
                name: "ck_links_disabled_kind_requires_at",
                schema: "links",
                table: "links",
                sql: "(disabled_at IS NULL) = (disabled_kind IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_links_generated_columns",
                schema: "links",
                table: "links",
                sql: "(is_custom AND code_length IS NULL AND code_number IS NULL AND key_version IS NULL) OR (NOT is_custom AND code_length IS NOT NULL AND code_number IS NOT NULL AND key_version IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "links");

            migrationBuilder.DropIndex(
                name: "ix_links_code_number",
                schema: "links",
                table: "links");

            migrationBuilder.DropIndex(
                name: "ix_links_purge_after",
                schema: "links",
                table: "links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_disabled_kind_requires_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_generated_columns",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "code_length",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "code_number",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "disabled_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "disabled_kind",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "disabled_reason",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "is_custom",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "key_version",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "purge_after",
                schema: "links",
                table: "links");
        }
    }
}
