using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkTargetKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec §3 / §2 migrator: fail fast (and let the migrator retry) instead of queueing behind live traffic
            // while this transaction waits for ACCESS EXCLUSIVE on links.links / links.access_policies.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AlterColumn<string>(
                name: "target_host",
                schema: "links",
                table: "links",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<string>(
                name: "destination_url",
                schema: "links",
                table: "links",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AddColumn<Guid>(
                name: "resource_id",
                schema: "links",
                table: "links",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "target_kind",
                schema: "links",
                table: "links",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "link_key_hash",
                schema: "links",
                table: "access_policies",
                type: "bytea",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "target_url",
                schema: "links",
                table: "abuse_evidence",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AddColumn<Guid>(
                name: "resource_id",
                schema: "links",
                table: "abuse_evidence",
                type: "uuid",
                nullable: true);

            // ARC-002 §1: constraints are only ADDED here, NOT VALID, so this transaction never scans links.links while
            // it holds ACCESS EXCLUSIVE (new and updated rows are checked from now on). Existing rows are validated by
            // the later migration ValidateLinkTargetKindChecks, one VALIDATE CONSTRAINT per non-transactional statement
            // (SHARE UPDATE EXCLUSIVE only, reads and writes continue). Every Phase 0 row satisfies them. The model
            // declares the same constraints (LinksDbContext) so the snapshot matches. The unique index on resource_id is
            // created CONCURRENTLY by AddLinkResourceIndex, outside any transaction.
            migrationBuilder.Sql("""
                ALTER TABLE links.links ADD CONSTRAINT ck_links_target_kind
                  CHECK (target_kind IN (0, 1, 2, 3)) NOT VALID;
                ALTER TABLE links.links ADD CONSTRAINT ck_links_target_resource
                  CHECK ((target_kind = 0) = (resource_id IS NULL)) NOT VALID;
                ALTER TABLE links.links ADD CONSTRAINT ck_links_target_destination
                  CHECK ((target_kind = 0) = (destination_url IS NOT NULL AND target_host IS NOT NULL)) NOT VALID;
                ALTER TABLE links.access_policies ADD CONSTRAINT ck_access_policies_link_key_hash
                  CHECK (link_key_hash IS NULL OR octet_length(link_key_hash) = 32) NOT VALID;
                """);
        }
        // Expand-only (spec MUST NOT #32): Down exists for local development only; never run it in production.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_target_destination",
                schema: "links",
                table: "links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_target_kind",
                schema: "links",
                table: "links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_target_resource",
                schema: "links",
                table: "links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_policies_link_key_hash",
                schema: "links",
                table: "access_policies");

            migrationBuilder.DropColumn(
                name: "resource_id",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "target_kind",
                schema: "links",
                table: "links");

            migrationBuilder.DropColumn(
                name: "link_key_hash",
                schema: "links",
                table: "access_policies");

            migrationBuilder.DropColumn(
                name: "resource_id",
                schema: "links",
                table: "abuse_evidence");

            migrationBuilder.AlterColumn<string>(
                name: "target_host",
                schema: "links",
                table: "links",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "destination_url",
                schema: "links",
                table: "links",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "target_url",
                schema: "links",
                table: "abuse_evidence",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);
        }
    }
}
