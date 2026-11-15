using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 E2.3 (docs/phase2/README.md): widens <c>ck_links_target_kind</c> to the team-tool kinds (drop 8, table 9, brief 10) and adds
    /// the admin panel's <c>guest-drop</c> and <c>guest-table</c> switches (constant defaults: no table rewrite). Expand-only (spec MUST NOT
    /// #32): the new check is added NOT VALID in the same transaction that drops the old one; ValidateTeamToolsTargetKind validates it
    /// outside any transaction (SHARE UPDATE EXCLUSIVE only).
    /// </summary>
    public partial class WidenTargetKindForTeamTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "drop_captcha",
                schema: "links",
                table: "guest_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "drop_enabled",
                schema: "links",
                table: "guest_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "table_captcha",
                schema: "links",
                table: "guest_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "table_enabled",
                schema: "links",
                table: "guest_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE links.links DROP CONSTRAINT IF EXISTS ck_links_target_kind;
                ALTER TABLE links.links ADD CONSTRAINT ck_links_target_kind CHECK (target_kind IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)) NOT VALID;
                """);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.DropColumn(
                name: "drop_captcha",
                schema: "links",
                table: "guest_settings");

            migrationBuilder.DropColumn(
                name: "drop_enabled",
                schema: "links",
                table: "guest_settings");

            migrationBuilder.DropColumn(
                name: "table_captcha",
                schema: "links",
                table: "guest_settings");

            migrationBuilder.DropColumn(
                name: "table_enabled",
                schema: "links",
                table: "guest_settings");

            migrationBuilder.Sql(
                """
                ALTER TABLE links.links DROP CONSTRAINT IF EXISTS ck_links_target_kind;
                ALTER TABLE links.links ADD CONSTRAINT ck_links_target_kind CHECK (target_kind IN (0, 1, 2, 3, 4, 5, 6, 7));
                """);
        }
    }
}
