using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <summary>
    /// ARC-002 §1 migration C: validates, for existing rows, the check constraints that AddLinkTargetKind added
    /// NOT VALID. VALIDATE CONSTRAINT takes only SHARE UPDATE EXCLUSIVE (reads and writes continue during the scan).
    /// Each statement runs on its own, outside any transaction (suppressTransaction: true), so no table lock taken
    /// by one validation is held while another table is scanned. Re-running VALIDATE on an already-valid constraint
    /// is a no-op, so databases that applied the earlier in-transaction version of AddLinkTargetKind are unaffected.
    /// </summary>
    public partial class ValidateLinkTargetKindChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_kind;",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_resource;",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_destination;",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TABLE links.access_policies VALIDATE CONSTRAINT ck_access_policies_link_key_hash;",
                suppressTransaction: true);
        }

        // Expand-only (spec MUST NOT #32): there is no schema change to undo; the constraints are dropped by
        // AddLinkTargetKind.Down (local development only).
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
