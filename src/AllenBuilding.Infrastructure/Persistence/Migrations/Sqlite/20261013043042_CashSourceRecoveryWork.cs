using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
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
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    attempt_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    provider_account = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    live_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    source_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    api_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    lease_token = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    cycle_started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    failures = table.Column<int>(type: "INTEGER", nullable: false),
                    first_failure_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    origin_trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    origin_span_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
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
                        name: "FK_cash_source_work_payment_attempts_attempt_id_account_id_provider_account_live_mode",
                        columns: x => new { x.attempt_id, x.account_id, x.provider_account, x.live_mode },
                        principalTable: "payment_attempts",
                        principalColumns: new[] { "id", "account_id", "provider_account", "live_mode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_source_work_actor_id_operation_id",
                table: "cash_source_work",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cash_source_work_attempt_id_account_id_provider_account_live_mode",
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
