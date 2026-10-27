using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestNoticeFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "expired_notice_queued_at",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expiring_notice_queued_at",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "purge_notice_queued_at",
                schema: "links",
                table: "links",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expired_notice_queued_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "expiring_notice_queued_at",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "purge_notice_queued_at",
                schema: "links",
                table: "links");
        }
    }
}
