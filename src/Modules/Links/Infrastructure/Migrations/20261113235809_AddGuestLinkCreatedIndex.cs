using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <summary>
    /// Guest-creation control panel stats (GET /api/admin/guest-controls/stats count guest links, uploads and snippets created
    /// in the last hour / 24 h / 7 days): a small partial index over guest rows only, so the count never scans account links.
    /// CREATE INDEX CONCURRENTLY cannot run inside a transaction, so this migration holds only that statement (same pattern as
    /// AddLinkResourceIndex). A failed concurrent build leaves an INVALID index: drop it concurrently and migrate again.
    /// </summary>
    public partial class AddGuestLinkCreatedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_links_guest_created_at ON links.links (created_at, target_kind) WHERE guest_creator_key IS NOT NULL;",
                suppressTransaction: true);
        }

        // Expand-only (spec MUST NOT #32): Down exists for local development only; never run it in production.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS links.ix_links_guest_created_at;", suppressTransaction: true);
        }
    }
}
