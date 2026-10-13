using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class IndependentTenancyClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ended_at",
                table: "rent_accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenancy_closures",
                columns: table => new
                {
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    membership_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    reviewed_membership_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    reviewed_account_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    requested_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenancy_closures", x => x.account_id);
                    table.ForeignKey(
                        name: "FK_tenancy_closures_asp_net_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenancy_closures_memberships_membership_id",
                        column: x => x.membership_id,
                        principalTable: "memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenancy_closures_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenancy_closures_actor_id_operation_id",
                table: "tenancy_closures",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenancy_closures_membership_id",
                table: "tenancy_closures",
                column: "membership_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenancy_closures_state_requested_at_account_id",
                table: "tenancy_closures",
                columns: new[] { "state", "requested_at", "account_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenancy_closures");

            migrationBuilder.DropColumn(
                name: "ended_at",
                table: "rent_accounts");
        }
    }
}
