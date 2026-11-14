using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Collaboration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Comparisons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "collab");

            migrationBuilder.CreateTable(
                name: "comparisons",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    after_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before_label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    after_label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comparisons", x => x.resource_id);
                    table.CheckConstraint("ck_comparisons_distinct_parts", "before_resource_id <> after_resource_id");
                    table.CheckConstraint("ck_comparisons_labels", "(before_label IS NULL OR char_length(before_label) <= 40) AND (after_label IS NULL OR char_length(after_label) <= 40)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_comparisons_owner",
                schema: "collab",
                table: "comparisons",
                columns: new[] { "owner_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comparisons",
                schema: "collab");
        }
    }
}
