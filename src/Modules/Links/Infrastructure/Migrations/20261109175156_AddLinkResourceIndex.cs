using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <summary>
    /// ARC-002 §1 migration B: exactly one link per resource (addendum §1). CREATE INDEX CONCURRENTLY cannot run inside
    /// a transaction, so this migration contains only that statement with suppressTransaction: true. If a concurrent
    /// build fails it leaves an INVALID index: run DROP INDEX CONCURRENTLY links.ux_links_resource_id and migrate again
    /// (IF NOT EXISTS would otherwise keep the invalid index).
    /// </summary>
    public partial class AddLinkResourceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_links_resource_id ON links.links (resource_id) WHERE resource_id IS NOT NULL;",
                suppressTransaction: true);
        }

        // Expand-only (spec MUST NOT #32): Down exists for local development only; never run it in production.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS links.ux_links_resource_id;", suppressTransaction: true);
        }
    }
}