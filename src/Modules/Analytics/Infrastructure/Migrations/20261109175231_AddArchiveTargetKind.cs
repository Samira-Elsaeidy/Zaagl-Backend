using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Analytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveTargetKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "target_url",
                schema: "analytics",
                table: "link_archive",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<short>(
                name: "target_kind",
                schema: "analytics",
                table: "link_archive",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "target_kind",
                schema: "analytics",
                table: "link_archive");

            migrationBuilder.AlterColumn<string>(
                name: "target_url",
                schema: "analytics",
                table: "link_archive",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
