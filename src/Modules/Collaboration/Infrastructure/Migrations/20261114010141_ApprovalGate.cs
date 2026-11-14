using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zaagl.Modules.Collaboration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "collab",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    watermark_text = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    watermark_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    release_on_approval = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approver_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    approver_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    approval_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    approver_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payment_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    released_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.resource_id);
                    table.CheckConstraint("ck_approvals_approved", "(state <> 1) OR approved_at IS NOT NULL");
                    table.CheckConstraint("ck_approvals_paid", "(state <> 2) OR paid_at IS NOT NULL");
                    table.CheckConstraint("ck_approvals_released", "(state = 3) = (released_at IS NOT NULL)");
                    table.CheckConstraint("ck_approvals_state", "state IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_approvals_watermark", "char_length(watermark_text) BETWEEN 1 AND 60");
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_draft",
                schema: "collab",
                table: "approvals",
                column: "draft_resource_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "collab");
        }
    }
}
