using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Links.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "links");

            migrationBuilder.CreateSequence(
                name: "slug_seq",
                schema: "links",
                startValue: 1000L);

            migrationBuilder.CreateTable(
                name: "folders",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "links",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    slug_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    public_stats = table.Column<bool>(type: "boolean", nullable: false),
                    og_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    og_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    og_image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "access_policies",
                schema: "links",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_clicks = table.Column<int>(type: "integer", nullable: true),
                    clicks_used = table.Column<int>(type: "integer", nullable: false),
                    fallback_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_policies", x => x.link_id);
                    table.ForeignKey(
                        name: "fk_access_policies_links_link_id",
                        column: x => x.link_id,
                        principalSchema: "links",
                        principalTable: "links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "destinations",
                schema: "links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    match = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    weight = table.Column<int>(type: "integer", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_destinations", x => x.id);
                    table.ForeignKey(
                        name: "fk_destinations_links_link_id",
                        column: x => x.link_id,
                        principalSchema: "links",
                        principalTable: "links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_destinations_link_id",
                schema: "links",
                table: "destinations",
                column: "link_id");

            migrationBuilder.CreateIndex(
                name: "ix_folders_owner_id_name",
                schema: "links",
                table: "folders",
                columns: new[] { "owner_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_links_folder_id",
                schema: "links",
                table: "links",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_links_owner_id_created_at",
                schema: "links",
                table: "links",
                columns: new[] { "owner_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_links_slug_key",
                schema: "links",
                table: "links",
                column: "slug_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_links_tags",
                schema: "links",
                table: "links",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_policies",
                schema: "links");

            migrationBuilder.DropTable(
                name: "destinations",
                schema: "links");

            migrationBuilder.DropTable(
                name: "folders",
                schema: "links");

            migrationBuilder.DropTable(
                name: "links",
                schema: "links");

            migrationBuilder.DropSequence(
                name: "slug_seq",
                schema: "links");
        }
    }
}
