using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CalendarBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rent_accounts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    membership_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rent_accounts", x => x.id);
                    table.ForeignKey(
                        name: "FK_rent_accounts_memberships_membership_id",
                        column: x => x.membership_id,
                        principalTable: "memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "billing_periods",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    starts_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_periods", x => x.id);
                    table.UniqueConstraint("AK_billing_periods_id_account_id", x => new { x.id, x.account_id });
                    table.ForeignKey(
                        name: "FK_billing_periods_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "billing_receipts",
                columns: table => new
                {
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_receipts", x => new { x.actor_id, x.operation_id });
                    table.ForeignKey(
                        name: "FK_billing_receipts_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rent_agreements",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    amount_minor_units = table.Column<long>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    due_day = table.Column<int>(type: "INTEGER", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    original_ends_on = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    superseded_by = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    created_by = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rent_agreements", x => x.id);
                    table.UniqueConstraint("AK_rent_agreements_id_account_id", x => new { x.id, x.account_id });
                    table.ForeignKey(
                        name: "FK_rent_agreements_asp_net_users_created_by",
                        column: x => x.created_by,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rent_agreements_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rent_agreements_rent_agreements_superseded_by_account_id",
                        columns: x => new { x.superseded_by, x.account_id },
                        principalTable: "rent_agreements",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "obligations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    period_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    agreement_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    amount_minor_units = table.Column<long>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    due_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    issued_by = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    issued_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_obligations", x => x.id);
                    table.ForeignKey(
                        name: "FK_obligations_asp_net_users_issued_by",
                        column: x => x.issued_by,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_obligations_billing_periods_period_id_account_id",
                        columns: x => new { x.period_id, x.account_id },
                        principalTable: "billing_periods",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_obligations_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_obligations_rent_agreements_agreement_id_account_id",
                        columns: x => new { x.agreement_id, x.account_id },
                        principalTable: "rent_agreements",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_periods_account_id_starts_on_ends_on_kind",
                table: "billing_periods",
                columns: new[] { "account_id", "starts_on", "ends_on", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_receipts_account_id",
                table: "billing_receipts",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_obligations_account_id_due_on_id",
                table: "obligations",
                columns: new[] { "account_id", "due_on", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_obligations_agreement_id_account_id",
                table: "obligations",
                columns: new[] { "agreement_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_obligations_issued_by",
                table: "obligations",
                column: "issued_by");

            migrationBuilder.CreateIndex(
                name: "IX_obligations_period_id",
                table: "obligations",
                column: "period_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_obligations_period_id_account_id",
                table: "obligations",
                columns: new[] { "period_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_rent_accounts_membership_id",
                table: "rent_accounts",
                column: "membership_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rent_agreements_account_id_starts_on_ends_on",
                table: "rent_agreements",
                columns: new[] { "account_id", "starts_on", "ends_on" });

            migrationBuilder.CreateIndex(
                name: "IX_rent_agreements_created_by",
                table: "rent_agreements",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_rent_agreements_superseded_by_account_id",
                table: "rent_agreements",
                columns: new[] { "superseded_by", "account_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_receipts");

            migrationBuilder.DropTable(
                name: "obligations");

            migrationBuilder.DropTable(
                name: "billing_periods");

            migrationBuilder.DropTable(
                name: "rent_agreements");

            migrationBuilder.DropTable(
                name: "rent_accounts");
        }
    }
}
