using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class InvitationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at",
                table: "invitations",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revoked_by",
                table: "invitations",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "token_version",
                table: "invitations",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "access_receipts",
                columns: table => new
                {
                    actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    payload_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    membership_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_receipts", x => new { x.actor_id, x.operation_id });
                    table.ForeignKey(
                        name: "FK_access_receipts_memberships_membership_id",
                        column: x => x.membership_id,
                        principalTable: "memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_access_receipts_membership_id",
                table: "access_receipts",
                column: "membership_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_receipts");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "revoked_by",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "token_version",
                table: "invitations");
        }
    }
}
