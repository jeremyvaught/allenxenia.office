using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
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
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    customer_binding_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    reviewed_account_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    agreement_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    agreement_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    provider_account = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    live_mode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    customer_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    monthly_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false, collation: "utf8mb4_bin"),
                    due_day = table.Column<int>(type: "int", nullable: false),
                    first_period_starts_on = table.Column<DateTime>(type: "date", nullable: false),
                    first_due_on = table.Column<DateTime>(type: "date", nullable: false),
                    first_anchor_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ends_before = table.Column<DateTime>(type: "date", nullable: true),
                    consented_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    consent_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    payment_method_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_bin"),
                    subscription_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_bin"),
                    last_verified_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    stop_operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb4_bin"),
                    stop_actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: true, collation: "utf8mb4_bin"),
                    stop_requested_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    stop_timing = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    stop_effective_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin")
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
                        name: "FK_autopay_authorizations_billing_customers_customer_binding_id~",
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
                        name: "FK_autopay_authorizations_rent_agreements_agreement_id_account_~",
                        columns: x => new { x.agreement_id, x.account_id },
                        principalTable: "rent_agreements",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "autopay_slots",
                columns: table => new
                {
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    authorization_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopay_slots", x => x.account_id);
                    table.ForeignKey(
                        name: "FK_autopay_slots_autopay_authorizations_authorization_id_accoun~",
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
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "autopay_work",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    authorization_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    stage = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    original_operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    reviewed_authorization_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    provider_operation_key = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopay_work", x => x.id);
                    table.ForeignKey(
                        name: "FK_autopay_work_autopay_authorizations_authorization_id_account~",
                        columns: x => new { x.authorization_id, x.account_id },
                        principalTable: "autopay_authorizations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_autopay_authorizations_account_id_state_first_period_starts_~",
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
                name: "IX_autopay_authorizations_customer_binding_id_account_id_provid~",
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
