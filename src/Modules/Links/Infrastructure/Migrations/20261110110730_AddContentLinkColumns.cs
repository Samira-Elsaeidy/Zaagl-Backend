using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentLinkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lifecycle_outbox",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    @event = table.Column<short>(name: "event", type: "smallint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lifecycle_outbox", x => x.id);
                    table.CheckConstraint("ck_lifecycle_outbox_event", "event IN (1, 2, 3, 4, 5, 6)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_lifecycle_outbox_link_id",
                schema: "links",
                table: "lifecycle_outbox",
                column: "link_id");

            migrationBuilder.CreateIndex(
                name: "ix_lifecycle_outbox_pending",
                schema: "links",
                table: "lifecycle_outbox",
                column: "occurred_at",
                filter: "processed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lifecycle_outbox",
                schema: "links");
        }
    }
}
