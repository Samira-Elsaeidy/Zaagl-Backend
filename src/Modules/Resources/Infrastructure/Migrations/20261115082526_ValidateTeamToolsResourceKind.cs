using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Resources.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 E2.3: validates <c>ck_resources_kind</c> (added NOT VALID by the previous migration) on its own, outside any transaction
    /// (SHARE UPDATE EXCLUSIVE only; re-running it on a valid constraint is a no-op).
    /// </summary>
    public partial class ValidateTeamToolsResourceKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE resources.resources VALIDATE CONSTRAINT ck_resources_kind;", suppressTransaction: true);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
