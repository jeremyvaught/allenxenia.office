using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class CashSourceRecoveryWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_source_work",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    attempt_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    provider_account = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    live_mode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    source_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    api_version = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    lease_token = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    lease_until = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    cycle_started_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    failures = table.Column<int>(type: "int", nullable: false),
                    first_failure_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    last_error = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_bin"),
                    origin_trace_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_bin"),
                    origin_span_id = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_source_work", x => x.id);
                    table.ForeignKey(
                        name: "FK_cash_source_work_asp_net_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_source_work_payment_attempts_attempt_id_account_id_prov~",
                        columns: x => new { x.attempt_id, x.account_id, x.provider_account, x.live_mode },
                        principalTable: "payment_attempts",
                        principalColumns: new[] { "id", "account_id", "provider_account", "live_mode" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_cash_source_work_actor_id_operation_id",
                table: "cash_source_work",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cash_source_work_attempt_id_account_id_provider_account_live~",
                table: "cash_source_work",
                columns: new[] { "attempt_id", "account_id", "provider_account", "live_mode" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_source_work_attempt_id_source_id",
                table: "cash_source_work",
                columns: new[] { "attempt_id", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cash_source_work_state_next_attempt_at_created_at_id",
                table: "cash_source_work",
                columns: new[] { "state", "next_attempt_at", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_source_work");
        }
    }
}
