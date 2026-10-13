using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AutopayConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "autopay_authorizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    customer_binding_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    reviewed_account_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    agreement_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    agreement_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    provider_account = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    live_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    monthly_minor_units = table.Column<long>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    due_day = table.Column<int>(type: "INTEGER", nullable: false),
                    first_period_starts_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    first_due_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    first_anchor_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ends_before = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    consented_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    consent_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    payment_method_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    subscription_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    last_verified_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    stop_operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    stop_actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    stop_requested_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    stop_timing = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    stop_effective_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopay_authorizations", x => x.id);
                    table.UniqueConstraint("AK_autopay_authorizations_id_account_id", x => new { x.id, x.account_id });
                    table.ForeignKey(
                        name: "FK_autopay_authorizations_asp_net_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_autopay_authorizations_asp_net_users_stop_actor_id",
                        column: x => x.stop_actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_autopay_authorizations_billing_customers_customer_binding_id_account_id_provider_account_live_mode_customer_id",
                        columns: x => new { x.customer_binding_id, x.account_id, x.provider_account, x.live_mode, x.customer_id },
                        principalTable: "billing_customers",
                        principalColumns: new[] { "id", "account_id", "provider_account", "live_mode", "customer_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_autopay_authorizations_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_autopay_authorizations_rent_agreements_agreement_id_account_id",
                        columns: x => new { x.agreement_id, x.account_id },
                        principalTable: "rent_agreements",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "autopay_slots",
                columns: table => new
                {
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    authorization_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopay_slots", x => x.account_id);
                    table.ForeignKey(
                        name: "FK_autopay_slots_autopay_authorizations_authorization_id_account_id",
                        columns: x => new { x.authorization_id, x.account_id },
                        principalTable: "autopay_authorizations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_autopay_slots_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "autopay_work",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    authorization_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    original_operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    reviewed_authorization_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    provider_operation_key = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopay_work", x => x.id);
                    table.ForeignKey(
                        name: "FK_autopay_work_autopay_authorizations_authorization_id_account_id",
                        columns: x => new { x.authorization_id, x.account_id },
                        principalTable: "autopay_authorizations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_autopay_authorizations_account_id_state_first_period_starts_on",
                table: "autopay_authorizations",
                columns: new[] { "account_id", "state", "first_period_starts_on" });

            migrationBuilder.CreateIndex(
                name: "IX_autopay_authorizations_actor_id_operation_id",
                table: "autopay_authorizations",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_autopay_authorizations_agreement_id_account_id",
                table: "autopay_authorizations",
                columns: new[] { "agreement_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_autopay_authorizations_customer_binding_id_account_id_provider_account_live_mode_customer_id",
                table: "autopay_authorizations",
                columns: new[] { "customer_binding_id", "account_id", "provider_account", "live_mode", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_autopay_authorizations_stop_actor_id",
                table: "autopay_authorizations",
                column: "stop_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_autopay_slots_authorization_id",
                table: "autopay_slots",
                column: "authorization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_autopay_slots_authorization_id_account_id",
                table: "autopay_slots",
                columns: new[] { "authorization_id", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_autopay_work_authorization_id_account_id",
                table: "autopay_work",
                columns: new[] { "authorization_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_autopay_work_authorization_id_stage_original_operation_id",
                table: "autopay_work",
                columns: new[] { "authorization_id", "stage", "original_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_autopay_work_state_created_at_id",
                table: "autopay_work",
                columns: new[] { "state", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "autopay_slots");

            migrationBuilder.DropTable(
                name: "autopay_work");

            migrationBuilder.DropTable(
                name: "autopay_authorizations");
        }
    }
}
