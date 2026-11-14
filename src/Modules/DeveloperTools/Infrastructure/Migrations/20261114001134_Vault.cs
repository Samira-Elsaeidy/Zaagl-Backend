using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zaagl.Modules.DeveloperTools.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Vault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "devtools");

            migrationBuilder.CreateTable(
                name: "secret_events",
                schema: "devtools",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    views_used = table.Column<short>(type: "smallint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secret_events", x => x.id);
                    table.CheckConstraint("ck_secret_events_kind", "kind IN ('created', 'revealed', 'burned', 'deleted', 'expired', 'disabled', 'purged')");
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                schema: "devtools",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    ciphertext_bytes = table.Column<int>(type: "integer", nullable: false),
                    envelope_version = table.Column<short>(type: "smallint", nullable: false),
                    passphrase_protected = table.Column<bool>(type: "boolean", nullable: false),
                    max_views = table.Column<short>(type: "smallint", nullable: false),
                    views_used = table.Column<short>(type: "smallint", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_viewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    wiped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    wipe_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => x.resource_id);
                    table.CheckConstraint("ck_secrets_ciphertext_bytes", "ciphertext_bytes BETWEEN 30 AND 1048576");
                    table.CheckConstraint("ck_secrets_ciphertext_length", "ciphertext IS NULL OR octet_length(ciphertext) = ciphertext_bytes");
                    table.CheckConstraint("ck_secrets_envelope_version", "envelope_version BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_secrets_max_views", "max_views BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_secrets_views_used", "views_used BETWEEN 0 AND max_views");
                    table.CheckConstraint("ck_secrets_wipe_reason", "wipe_reason IS NULL OR wipe_reason IN ('burned', 'deleted', 'expired', 'disabled', 'purged')");
                    table.CheckConstraint("ck_secrets_wiped", "(ciphertext IS NULL) = (wiped_at IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_secret_events_resource_id_id",
                schema: "devtools",
                table: "secret_events",
                columns: new[] { "resource_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_secrets_live_expiry",
                schema: "devtools",
                table: "secrets",
                column: "expires_at",
                filter: "ciphertext IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_secrets_owner",
                schema: "devtools",
                table: "secrets",
                columns: new[] { "owner_id", "created_at" },
                filter: "owner_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "secret_events",
                schema: "devtools");

            migrationBuilder.DropTable(
                name: "secrets",
                schema: "devtools");
        }
    }
}
