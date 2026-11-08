using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Analytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClickEventUtm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "utm_campaign",
                schema: "analytics",
                table: "click_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_content",
                schema: "analytics",
                table: "click_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_medium",
                schema: "analytics",
                table: "click_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_source",
                schema: "analytics",
                table: "click_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_term",
                schema: "analytics",
                table: "click_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "utm_campaign",
                schema: "analytics",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "utm_content",
                schema: "analytics",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "utm_medium",
                schema: "analytics",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "utm_source",
                schema: "analytics",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "utm_term",
                schema: "analytics",
                table: "click_events");
        }
    }
}
