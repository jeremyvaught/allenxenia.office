using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class SettlementAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_payment_attempts_id_account_id_provider_account_live_mode",
                table: "payment_attempts",
                columns: new[] { "id", "account_id", "provider_account", "live_mode" });

            migrationBuilder.CreateTable(
                name: "settlement_payments",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    attempt_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    provider_account = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    live_mode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    collection_reference = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    amount_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false, collation: "utf8mb4_bin"),
                    recorded_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement_payments", x => x.id);
                    table.UniqueConstraint("AK_settlement_payments_id_account_id_attempt_id", x => new { x.id, x.account_id, x.attempt_id });
                    table.CheckConstraint("CK_settlement_amount", "amount_minor_units > 0");
                    table.ForeignKey(
                        name: "FK_settlement_payments_payment_attempts_attempt_id_account_id_p~",
                        columns: x => new { x.attempt_id, x.account_id, x.provider_account, x.live_mode },
                        principalTable: "payment_attempts",
                        principalColumns: new[] { "id", "account_id", "provider_account", "live_mode" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "payment_allocations",
                columns: table => new
                {
                    payment_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    obligation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    attempt_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    amount_minor_units = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_allocations", x => new { x.payment_id, x.obligation_id });
                    table.CheckConstraint("CK_allocation_amount", "amount_minor_units > 0");
                    table.ForeignKey(
                        name: "FK_payment_allocations_payment_attempt_items_attempt_id_obligat~",
                        columns: x => new { x.attempt_id, x.obligation_id, x.account_id },
                        principalTable: "payment_attempt_items",
                        principalColumns: new[] { "attempt_id", "obligation_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_allocations_settlement_payments_payment_id_account_i~",
                        columns: x => new { x.payment_id, x.account_id, x.attempt_id },
                        principalTable: "settlement_payments",
                        principalColumns: new[] { "id", "account_id", "attempt_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_account_id_obligation_id",
                table: "payment_allocations",
                columns: new[] { "account_id", "obligation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_attempt_id_obligation_id",
                table: "payment_allocations",
                columns: new[] { "attempt_id", "obligation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_attempt_id_obligation_id_account_id",
                table: "payment_allocations",
                columns: new[] { "attempt_id", "obligation_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_payment_id_account_id_attempt_id",
                table: "payment_allocations",
                columns: new[] { "payment_id", "account_id", "attempt_id" });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_payments_account_id_recorded_at_id",
                table: "settlement_payments",
                columns: new[] { "account_id", "recorded_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_payments_attempt_id",
                table: "settlement_payments",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_settlement_payments_attempt_id_account_id_provider_account_l~",
                table: "settlement_payments",
                columns: new[] { "attempt_id", "account_id", "provider_account", "live_mode" });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_payments_provider_account_live_mode_collection_re~",
                table: "settlement_payments",
                columns: new[] { "provider_account", "live_mode", "collection_reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_allocations");

            migrationBuilder.DropTable(
                name: "settlement_payments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_payment_attempts_id_account_id_provider_account_live_mode",
                table: "payment_attempts");
        }
    }
}
