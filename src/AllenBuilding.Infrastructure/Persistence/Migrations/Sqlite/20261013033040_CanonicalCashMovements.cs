using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CanonicalCashMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_movements",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    capture_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    attempt_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    provider_account = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    live_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    payment_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    source_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    transaction_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    signed_minor_units = table.Column<long>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    provider_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_movements", x => x.id);
                    table.UniqueConstraint("AK_cash_movements_id_account_id", x => new { x.id, x.account_id });
                    table.CheckConstraint("CK_cash_movement_amount", "signed_minor_units != 0 AND signed_minor_units >= -99999999 AND signed_minor_units <= 99999999 AND currency = 'USD' AND ((kind = 'DisputeReinstatement' AND signed_minor_units > 0) OR (kind IN ('Refund', 'DisputeWithdrawal') AND signed_minor_units < 0))");
                    table.ForeignKey(
                        name: "FK_cash_movements_payment_attempts_attempt_id_account_id_provider_account_live_mode",
                        columns: x => new { x.attempt_id, x.account_id, x.provider_account, x.live_mode },
                        principalTable: "payment_attempts",
                        principalColumns: new[] { "id", "account_id", "provider_account", "live_mode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_movements_settlement_payments_capture_id_account_id_attempt_id",
                        columns: x => new { x.capture_id, x.account_id, x.attempt_id },
                        principalTable: "settlement_payments",
                        principalColumns: new[] { "id", "account_id", "attempt_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_movement_reviews",
                columns: table => new
                {
                    movement_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_movement_reviews", x => x.movement_id);
                    table.ForeignKey(
                        name: "FK_cash_movement_reviews_cash_movements_movement_id_account_id",
                        columns: x => new { x.movement_id, x.account_id },
                        principalTable: "cash_movements",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_movement_reviews_account_id_state_created_at",
                table: "cash_movement_reviews",
                columns: new[] { "account_id", "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_movement_reviews_movement_id_account_id",
                table: "cash_movement_reviews",
                columns: new[] { "movement_id", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_account_id_recorded_at_id",
                table: "cash_movements",
                columns: new[] { "account_id", "recorded_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_attempt_id_account_id_provider_account_live_mode",
                table: "cash_movements",
                columns: new[] { "attempt_id", "account_id", "provider_account", "live_mode" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_capture_id_account_id_attempt_id",
                table: "cash_movements",
                columns: new[] { "capture_id", "account_id", "attempt_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_provider_account_live_mode_transaction_id",
                table: "cash_movements",
                columns: new[] { "provider_account", "live_mode", "transaction_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_movement_reviews");

            migrationBuilder.DropTable(
                name: "cash_movements");
        }
    }
}
