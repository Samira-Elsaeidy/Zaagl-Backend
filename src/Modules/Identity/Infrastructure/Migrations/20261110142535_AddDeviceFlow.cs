using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_activation_failures",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_activation_failures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_authorizations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    client_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    device_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    os = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_polled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    poll_interval_seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_authorizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_sessions",
                schema: "identity",
                columns: table => new
                {
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    device_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    os = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_sessions", x => x.family_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_activation_failures_user_id_created_at",
                schema: "identity",
                table: "device_activation_failures",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_device_authorizations_device_code_hash",
                schema: "identity",
                table: "device_authorizations",
                column: "device_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_authorizations_expires_at",
                schema: "identity",
                table: "device_authorizations",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_authorizations_user_code_hash",
                schema: "identity",
                table: "device_authorizations",
                column: "user_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_sessions_user_id",
                schema: "identity",
                table: "device_sessions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_activation_failures",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "device_authorizations",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "device_sessions",
                schema: "identity");
        }
    }
}
