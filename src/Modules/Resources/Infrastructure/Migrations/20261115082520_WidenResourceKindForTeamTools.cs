using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Resources.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 E2.3 (docs/phase2/README.md): widens <c>ck_resources_kind</c> to drop 8, table 9 and brief 10. Expand-only: added NOT VALID
    /// in the transaction that drops the old check; ValidateTeamToolsResourceKind validates it outside any transaction.
    /// </summary>
    public partial class WidenResourceKindForTeamTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE resources.resources DROP CONSTRAINT IF EXISTS ck_resources_kind;
                ALTER TABLE resources.resources ADD CONSTRAINT ck_resources_kind CHECK (kind IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)) NOT VALID;
                """);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE resources.resources DROP CONSTRAINT IF EXISTS ck_resources_kind;
                ALTER TABLE resources.resources ADD CONSTRAINT ck_resources_kind CHECK (kind IN (1, 2, 3, 4, 5, 6, 7));
                """);
        }
    }
}
