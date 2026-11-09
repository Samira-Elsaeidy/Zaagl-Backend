using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Resources.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "resources");

            migrationBuilder.CreateTable(
                name: "quota_ledger",
                schema: "resources",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    reserved_bytes = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_ledger", x => x.owner_id);
                    table.CheckConstraint("ck_quota_ledger_reserved", "reserved_bytes >= 0");
                    table.CheckConstraint("ck_quota_ledger_used", "used_bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "quota_reservations",
                schema: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_reservations", x => x.id);
                    table.CheckConstraint("ck_quota_reservations_bytes", "bytes >= 0");
                    table.CheckConstraint("ck_quota_reservations_state", "state IN (0, 1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "quota_usage",
                schema: "resources",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    released_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_usage", x => x.resource_id);
                    table.CheckConstraint("ck_quota_usage_bytes", "bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "resources",
                schema: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_creator_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    link_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    declared_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    processing_status = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    rejected_reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    burn_after_download = table.Column<bool>(type: "boolean", nullable: false),
                    burn_claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    burn_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    link_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    link_purge_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    bytes_state = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    legal_hold_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_via = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resources", x => x.id);
                    table.CheckConstraint("ck_resources_bytes_state", "bytes_state IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_resources_declared_size", "declared_size_bytes >= 0");
                    table.CheckConstraint("ck_resources_kind", "kind IN (1, 2, 3)");
                    table.CheckConstraint("ck_resources_owner_xor_guest", "(owner_id IS NULL) <> (guest_creator_key IS NULL)");
                    table.CheckConstraint("ck_resources_size", "size_bytes IS NULL OR size_bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "download_grants",
                schema: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<short>(type: "smallint", nullable: false),
                    minted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    first_use_by = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    first_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    is_burn_claim = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_download_grants_resources_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "resources",
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_grants_resource_id",
                schema: "resources",
                table: "download_grants",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "ix_quota_reservations_owner_id_state",
                schema: "resources",
                table: "quota_reservations",
                columns: new[] { "owner_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_quota_usage_owner_id",
                schema: "resources",
                table: "quota_usage",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_resources_link_id",
                schema: "resources",
                table: "resources",
                column: "link_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_resources_owner_created",
                schema: "resources",
                table: "resources",
                columns: new[] { "owner_id", "created_at" },
                descending: new[] { false, true },
                filter: "owner_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_resources_public_id",
                schema: "resources",
                table: "resources",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_resources_unbound_created",
                schema: "resources",
                table: "resources",
                column: "created_at",
                filter: "link_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "download_grants",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "quota_ledger",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "quota_reservations",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "quota_usage",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "resources",
                schema: "resources");
        }
    }
}
