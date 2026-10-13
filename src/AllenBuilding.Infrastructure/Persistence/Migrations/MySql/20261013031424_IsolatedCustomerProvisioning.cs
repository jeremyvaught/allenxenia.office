using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
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
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    provider_account = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    live_mode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    api_version = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    dispatched_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    customer_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_bin"),
                    version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    lease_token = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    lease_until = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    failures = table.Column<int>(type: "int", nullable: false),
                    last_error = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_bin"),
                    origin_trace_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_bin"),
                    origin_span_id = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_bin")
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
                })
                .Annotation("MySQL:Charset", "utf8mb4");

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
