using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Files.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocked_content_hashes",
                schema: "files",
                columns: table => new
                {
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocked_content_hashes", x => x.sha256);
                    table.CheckConstraint("ck_blocked_content_hashes_sha256", "octet_length(sha256) = 32");
                });

            migrationBuilder.CreateTable(
                name: "storage_objects",
                schema: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<short>(type: "smallint", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    detected_mime = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: true),
                    detected_ext = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    scan_status = table.Column<short>(type: "smallint", nullable: false),
                    scan_signature = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    scan_engine = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    scanned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_objects", x => x.id);
                    table.CheckConstraint("ck_storage_objects_area", "area IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("ck_storage_objects_scan", "scan_status IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_storage_objects_sha256", "sha256 IS NULL OR octet_length(sha256) = 32");
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                schema: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_creator_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    declared_length = table.Column<long>(type: "bigint", nullable: false),
                    received_length = table.Column<long>(type: "bigint", nullable: false),
                    declared_name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    declared_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    intended_kind = table.Column<short>(type: "smallint", nullable: false),
                    max_bytes = table.Column<long>(type: "bigint", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_via = table.Column<short>(type: "smallint", nullable: false),
                    create_key_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    complete_key_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    complete_body_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    complete_response = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_sessions", x => x.id);
                    table.CheckConstraint("ck_upload_sessions_declared_length", "declared_length >= 1");
                    table.CheckConstraint("ck_upload_sessions_owner_xor_guest", "(owner_id IS NULL) <> (guest_creator_key IS NULL)");
                    table.CheckConstraint("ck_upload_sessions_received_length", "received_length BETWEEN 0 AND declared_length");
                    table.CheckConstraint("ck_upload_sessions_status", "status IN (0, 1, 2, 3, 4)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_storage_objects_resource_id",
                schema: "files",
                table: "storage_objects",
                column: "resource_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_storage_objects_sha256",
                schema: "files",
                table: "storage_objects",
                column: "sha256",
                filter: "sha256 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_owner_created",
                schema: "files",
                table: "upload_sessions",
                columns: new[] { "owner_id", "created_at" },
                descending: new[] { false, true },
                filter: "owner_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_status_expires",
                schema: "files",
                table: "upload_sessions",
                columns: new[] { "status", "expires_at" },
                filter: "status IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "ux_upload_sessions_owner_create_key",
                schema: "files",
                table: "upload_sessions",
                columns: new[] { "owner_id", "create_key_hash" },
                unique: true,
                filter: "create_key_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocked_content_hashes",
                schema: "files");

            migrationBuilder.DropTable(
                name: "storage_objects",
                schema: "files");

            migrationBuilder.DropTable(
                name: "upload_sessions",
                schema: "files");
        }
    }
}
