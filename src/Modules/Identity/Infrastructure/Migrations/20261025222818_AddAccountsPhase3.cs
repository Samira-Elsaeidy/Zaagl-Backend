using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountsPhase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "disabled_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disabled_reason",
                schema: "identity",
                table: "users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "email_confirmed",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "role",
                schema: "identity",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                schema: "identity",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "totp_confirmed_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "totp_secret",
                schema: "identity",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "scopes",
                schema: "identity",
                table: "api_keys",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "email_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_failures",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ip_partition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_failures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_tokens_token_hash",
                schema: "identity",
                table: "email_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_tokens_user_id_kind",
                schema: "identity",
                table: "email_tokens",
                columns: new[] { "user_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_login_failures_user_id_ip_partition_created_at",
                schema: "identity",
                table: "login_failures",
                columns: new[] { "user_id", "ip_partition", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "login_failures",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "disabled_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "disabled_reason",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_confirmed",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_confirmed_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_secret",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "scopes",
                schema: "identity",
                table: "api_keys");
        }
    }
}
