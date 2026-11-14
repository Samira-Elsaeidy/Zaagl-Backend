using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Resources.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 (docs/phase2/README.md): widens <c>ck_resources_kind</c> to the four Phase 2 tool kinds (secret 4, capsule 5, compare 6,
    /// approval 7). Expand-only (spec MUST NOT #32): every existing row already satisfies the wider check. The new constraint is
    /// added NOT VALID (no table scan under ACCESS EXCLUSIVE) inside the transaction that drops the old one, so no window exists
    /// without a check; the next migration (Validate*) runs VALIDATE on its own, outside any transaction, taking only SHARE UPDATE EXCLUSIVE.
    /// </summary>
    public partial class WidenResourceKindForPhase2Tools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE resources.resources DROP CONSTRAINT IF EXISTS ck_resources_kind;
                ALTER TABLE resources.resources ADD CONSTRAINT ck_resources_kind CHECK (kind IN (1, 2, 3, 4, 5, 6, 7)) NOT VALID;
                """);
        }

        /// <summary>Local development only (expand-only in every shared environment).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE resources.resources DROP CONSTRAINT IF EXISTS ck_resources_kind;
                ALTER TABLE resources.resources ADD CONSTRAINT ck_resources_kind CHECK (kind IN (1, 2, 3));
                """);
        }
    }
}
