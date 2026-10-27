using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_outbox",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    send_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    digest_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_suppressions",
                schema: "identity",
                columns: table => new
                {
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_suppressions", x => x.email);
                });

            migrationBuilder.CreateTable(
                name: "email_outbox_items",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at_snapshot = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_outbox_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_outbox_items_email_outbox_outbox_id",
                        column: x => x.outbox_id,
                        principalSchema: "identity",
                        principalTable: "email_outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_status_send_after",
                schema: "identity",
                table: "email_outbox",
                columns: new[] { "status", "send_after" });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_user_id_kind_digest_date",
                schema: "identity",
                table: "email_outbox",
                columns: new[] { "user_id", "kind", "digest_date" },
                unique: true,
                filter: "digest_date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_items_outbox_id",
                schema: "identity",
                table: "email_outbox_items",
                column: "outbox_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_outbox_items",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "email_suppressions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "email_outbox",
                schema: "identity");
        }
    }
}
