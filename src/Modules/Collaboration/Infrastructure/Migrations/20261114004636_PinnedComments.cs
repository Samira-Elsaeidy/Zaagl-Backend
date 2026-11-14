using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zaagl.Modules.Collaboration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PinnedComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comment_settings",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    allow_anonymous = table.Column<bool>(type: "boolean", nullable: false),
                    comment_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_settings", x => x.resource_id);
                    table.CheckConstraint("ck_comment_settings_count", "comment_count BETWEEN 0 AND 10000");
                });

            migrationBuilder.CreateTable(
                name: "comments",
                schema: "collab",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    side = table.Column<short>(type: "smallint", nullable: true),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    author_kind = table.Column<short>(type: "smallint", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.CheckConstraint("ck_comments_author", "author_kind IN (0, 1, 2) AND ((author_kind = 2) = (author_user_id IS NULL))");
                    table.CheckConstraint("ck_comments_body", "char_length(body) BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_comments_point", "x BETWEEN 0 AND 1 AND y BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_comments_side", "side IS NULL OR side IN (0, 1)");
                    table.CheckConstraint("ck_comments_status", "status IN (0, 1) AND ((status = 1) = (resolved_at IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_comments_comment_settings_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "collab",
                        principalTable: "comment_settings",
                        principalColumn: "resource_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comments_author",
                schema: "collab",
                table: "comments",
                column: "author_user_id",
                filter: "author_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_comments_resource",
                schema: "collab",
                table: "comments",
                columns: new[] { "resource_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comments",
                schema: "collab");

            migrationBuilder.DropTable(
                name: "comment_settings",
                schema: "collab");
        }
    }
}
