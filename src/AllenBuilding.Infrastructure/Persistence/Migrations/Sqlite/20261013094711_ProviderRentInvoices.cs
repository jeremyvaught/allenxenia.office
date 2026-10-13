using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ProviderRentInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_rent_invoices",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    authorization_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    period_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    obligation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    provider_account = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    live_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    invoice_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    line_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    consent_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    line_starts_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    line_ends_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    original_minor_units = table.Column<long>(type: "INTEGER", nullable: false),
                    remaining_minor_units = table.Column<long>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    auto_advance = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    checked_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_rent_invoices", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_rent_invoices_autopay_authorizations_authorization_id_account_id",
                        columns: x => new { x.authorization_id, x.account_id },
                        principalTable: "autopay_authorizations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_rent_invoices_billing_periods_period_id_account_id",
                        columns: x => new { x.period_id, x.account_id },
                        principalTable: "billing_periods",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_rent_invoices_obligations_obligation_id_account_id",
                        columns: x => new { x.obligation_id, x.account_id },
                        principalTable: "obligations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_authorization_id_account_id",
                table: "provider_rent_invoices",
                columns: new[] { "authorization_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_authorization_id_checked_at",
                table: "provider_rent_invoices",
                columns: new[] { "authorization_id", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_obligation_id",
                table: "provider_rent_invoices",
                column: "obligation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_obligation_id_account_id",
                table: "provider_rent_invoices",
                columns: new[] { "obligation_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_period_id",
                table: "provider_rent_invoices",
                column: "period_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_period_id_account_id",
                table: "provider_rent_invoices",
                columns: new[] { "period_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_rent_invoices_provider_account_live_mode_invoice_id",
                table: "provider_rent_invoices",
                columns: new[] { "provider_account", "live_mode", "invoice_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_rent_invoices");
        }
    }
}
