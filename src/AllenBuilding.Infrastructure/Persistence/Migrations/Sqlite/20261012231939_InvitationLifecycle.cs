using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revoked_by",
                table: "invitations",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "token_version",
                table: "invitations",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "access_receipts",
                columns: table => new
                {
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    membership_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                });

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
