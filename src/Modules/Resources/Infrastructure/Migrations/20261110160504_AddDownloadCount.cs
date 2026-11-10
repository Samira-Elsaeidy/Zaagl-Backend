using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Resources.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "download_count",
                schema: "resources",
                table: "resources",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_resources_burn_claimed",
                schema: "resources",
                table: "resources",
                column: "burn_claimed_at",
                filter: "burn_claimed_at IS NOT NULL AND bytes_state = 1");

            migrationBuilder.CreateIndex(
                name: "ix_resources_purge_due",
                schema: "resources",
                table: "resources",
                column: "link_purge_after",
                filter: "bytes_state IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "ix_download_grants_first_use_by",
                schema: "resources",
                table: "download_grants",
                column: "first_use_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_resources_burn_claimed",
                schema: "resources",
                table: "resources");

            migrationBuilder.DropIndex(
                name: "ix_resources_purge_due",
                schema: "resources",
                table: "resources");

            migrationBuilder.DropIndex(
                name: "ix_download_grants_first_use_by",
                schema: "resources",
                table: "download_grants");

            migrationBuilder.DropColumn(
                name: "download_count",
                schema: "resources",
                table: "resources");
        }
    }
}
