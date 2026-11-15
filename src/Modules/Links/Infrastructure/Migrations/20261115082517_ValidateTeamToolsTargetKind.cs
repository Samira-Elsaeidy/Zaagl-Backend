using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 E2.3: validates <c>ck_links_target_kind</c> (added NOT VALID by WidenTargetKindForTeamTools) on its own, outside any
    /// transaction (SHARE UPDATE EXCLUSIVE only; re-running it on a valid constraint is a no-op).
    /// </summary>
    public partial class ValidateTeamToolsTargetKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_kind;", suppressTransaction: true);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
