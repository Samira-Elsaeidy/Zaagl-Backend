using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Resources.Infrastructure.Migrations
{
    /// <summary>
    /// Phase 2 (docs/phase2): validates <c>ck_resources_kind</c>, added NOT VALID by the previous migration. Its own migration, outside any transaction
    /// (suppressTransaction), like ValidateLinkTargetKindChecks: VALIDATE takes only SHARE UPDATE EXCLUSIVE (reads and writes continue), and
    /// re-running it on a valid constraint is a no-op.
    /// </summary>
    public partial class ValidatePhase2ResourceKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE resources.resources VALIDATE CONSTRAINT ck_resources_kind;", suppressTransaction: true);
        }

        /// <summary>Expand-only: nothing to undo.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
