using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class IsolatedCustomerProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_provisions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    provider_account = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    live_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    api_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    lease_token = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    failures = table.Column<int>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    origin_trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    origin_span_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_provisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_provisions_asp_net_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_provisions_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_provisions_account_id_provider_account_live_mode",
                table: "customer_provisions",
                columns: new[] { "account_id", "provider_account", "live_mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_provisions_actor_id",
                table: "customer_provisions",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_provisions_state_next_attempt_at_created_at_id",
                table: "customer_provisions",
                columns: new[] { "state", "next_attempt_at", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_provisions");
        }
    }
}
