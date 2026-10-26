using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "owner_id",
                schema: "links",
                table: "links",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<long>(
                name: "click_count",
                schema: "links",
                table: "links",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "guest_creator_key",
                schema: "links",
                table: "links",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_links_owner_xor_guest",
                schema: "links",
                table: "links",
                sql: "(owner_id IS NULL) <> (guest_creator_key IS NULL)");

            // Spec §7.12: hourly buckets for the guest quota's rolling 24h window. Not an EF-mapped entity
            // (GuestQuotaStore only ever reaches it through raw SQL, like idempotency_keys' INSERT path).
            migrationBuilder.Sql("""
                CREATE TABLE links.guest_quota (
                    prefix_key bytea NOT NULL,
                    hour timestamptz NOT NULL,
                    count integer NOT NULL DEFAULT 0,
                    PRIMARY KEY (prefix_key, hour)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE links.guest_quota;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_owner_xor_guest",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "click_count",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "guest_creator_key",
                schema: "links",
                table: "links");

            migrationBuilder.AlterColumn<Guid>(
                name: "owner_id",
                schema: "links",
                table: "links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
