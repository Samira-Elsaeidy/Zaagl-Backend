using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Collaboration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClipboardTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tables",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_format = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    column_count = table.Column<int>(type: "integer", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    document_bytes = table.Column<int>(type: "integer", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tables", x => x.resource_id);
                    table.CheckConstraint("ck_tables_format", "source_format IN ('json', 'csv')");
                    table.CheckConstraint("ck_tables_size", "column_count BETWEEN 1 AND 500 AND row_count BETWEEN 0 AND 200000 AND document_bytes BETWEEN 2 AND 33554432");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tables_owner",
                schema: "collab",
                table: "tables",
                columns: new[] { "owner_id", "created_at" },
                filter: "owner_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tables",
                schema: "collab");
        }
    }
}
