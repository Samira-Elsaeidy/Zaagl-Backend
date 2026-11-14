using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zaagl.Modules.DeveloperTools.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WebhookCatcher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hooks",
                schema: "devtools",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false, collation: "C"),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    response_status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    purge_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    max_requests = table.Column<int>(type: "integer", nullable: false),
                    max_body_bytes = table.Column<int>(type: "integer", nullable: false),
                    max_stored_bytes = table.Column<long>(type: "bigint", nullable: false),
                    ingest_per_minute = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    window_count = table.Column<int>(type: "integer", nullable: false),
                    received_total = table.Column<long>(type: "bigint", nullable: false),
                    stored_count = table.Column<int>(type: "integer", nullable: false),
                    stored_bytes = table.Column<long>(type: "bigint", nullable: false),
                    last_received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hooks", x => x.id);
                    table.CheckConstraint("ck_hooks_counters", "window_count >= 0 AND received_total >= 0 AND stored_count >= 0 AND stored_bytes >= 0");
                    table.CheckConstraint("ck_hooks_dates", "expires_at > created_at AND purge_after >= expires_at");
                    table.CheckConstraint("ck_hooks_limits", "max_requests BETWEEN 1 AND 10000 AND max_body_bytes BETWEEN 0 AND 1048576 AND max_stored_bytes >= 0 AND ingest_per_minute >= 1");
                    table.CheckConstraint("ck_hooks_response_status", "response_status BETWEEN 200 AND 299 OR response_status BETWEEN 400 AND 599");
                    table.CheckConstraint("ck_hooks_token", "token ~ '^[A-Za-z0-9_-]{22}$'");
                });

            migrationBuilder.CreateTable(
                name: "hook_requests",
                schema: "devtools",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    query = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    body = table.Column<byte[]>(type: "bytea", nullable: true),
                    body_bytes_total = table.Column<long>(type: "bigint", nullable: false),
                    body_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    stored_bytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hook_requests", x => x.id);
                    table.CheckConstraint("ck_hook_requests_sizes", "body_bytes_total >= 0 AND stored_bytes >= 0 AND (body IS NULL OR octet_length(body) <= 1048576)");
                    table.ForeignKey(
                        name: "fk_hook_requests_hooks_hook_id",
                        column: x => x.hook_id,
                        principalSchema: "devtools",
                        principalTable: "hooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hook_requests_hook",
                schema: "devtools",
                table: "hook_requests",
                columns: new[] { "hook_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_hooks_owner_expiry",
                schema: "devtools",
                table: "hooks",
                columns: new[] { "owner_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_hooks_purge_after",
                schema: "devtools",
                table: "hooks",
                column: "purge_after");

            migrationBuilder.CreateIndex(
                name: "uq_hooks_token",
                schema: "devtools",
                table: "hooks",
                column: "token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hook_requests",
                schema: "devtools");

            migrationBuilder.DropTable(
                name: "hooks",
                schema: "devtools");
        }
    }
}
