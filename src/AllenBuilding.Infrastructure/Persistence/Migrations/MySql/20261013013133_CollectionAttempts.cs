using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class CollectionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_obligations_id_account_id",
                table: "obligations",
                columns: new[] { "id", "account_id" });

            migrationBuilder.CreateTable(
                name: "billing_customers",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    provider_account = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    live_mode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    customer_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    bound_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_customers", x => x.id);
                    table.UniqueConstraint("AK_billing_customers_id_account_id_provider_account_live_mode_c~", x => new { x.id, x.account_id, x.provider_account, x.live_mode, x.customer_id });
                    table.ForeignKey(
                        name: "FK_billing_customers_rent_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "rent_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    customer_binding_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    provider_account = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    live_mode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    customer_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    total_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false, collation: "utf8mb4_bin"),
                    payload_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    session_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_bin"),
                    payment_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempts", x => x.id);
                    table.UniqueConstraint("AK_payment_attempts_id_account_id", x => new { x.id, x.account_id });
                    table.ForeignKey(
                        name: "FK_payment_attempts_asp_net_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_attempts_billing_customers_customer_binding_id_accou~",
                        columns: x => new { x.customer_binding_id, x.account_id, x.provider_account, x.live_mode, x.customer_id },
                        principalTable: "billing_customers",
                        principalColumns: new[] { "id", "account_id", "provider_account", "live_mode", "customer_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "payment_attempt_items",
                columns: table => new
                {
                    attempt_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    obligation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    reviewed_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    amount_minor_units = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempt_items", x => new { x.attempt_id, x.obligation_id });
                    table.UniqueConstraint("AK_payment_attempt_items_attempt_id_obligation_id_account_id", x => new { x.attempt_id, x.obligation_id, x.account_id });
                    table.ForeignKey(
                        name: "FK_payment_attempt_items_obligations_obligation_id_account_id",
                        columns: x => new { x.obligation_id, x.account_id },
                        principalTable: "obligations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_attempt_items_payment_attempts_attempt_id_account_id",
                        columns: x => new { x.attempt_id, x.account_id },
                        principalTable: "payment_attempts",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "collection_guards",
                columns: table => new
                {
                    obligation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    attempt_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_guards", x => x.obligation_id);
                    table.ForeignKey(
                        name: "FK_collection_guards_payment_attempt_items_attempt_id_obligatio~",
                        columns: x => new { x.attempt_id, x.obligation_id, x.account_id },
                        principalTable: "payment_attempt_items",
                        principalColumns: new[] { "attempt_id", "obligation_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_billing_customers_account_id_provider_account_live_mode",
                table: "billing_customers",
                columns: new[] { "account_id", "provider_account", "live_mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_customers_provider_account_live_mode_customer_id",
                table: "billing_customers",
                columns: new[] { "provider_account", "live_mode", "customer_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collection_guards_attempt_id_obligation_id_account_id",
                table: "collection_guards",
                columns: new[] { "attempt_id", "obligation_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_items_attempt_id_account_id",
                table: "payment_attempt_items",
                columns: new[] { "attempt_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_items_obligation_id_account_id",
                table: "payment_attempt_items",
                columns: new[] { "obligation_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_actor_id_operation_id",
                table: "payment_attempts",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_customer_binding_id_account_id_provider_acc~",
                table: "payment_attempts",
                columns: new[] { "customer_binding_id", "account_id", "provider_account", "live_mode", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_provider_account_live_mode_payment_id",
                table: "payment_attempts",
                columns: new[] { "provider_account", "live_mode", "payment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_provider_account_live_mode_session_id",
                table: "payment_attempts",
                columns: new[] { "provider_account", "live_mode", "session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_state_created_at_id",
                table: "payment_attempts",
                columns: new[] { "state", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_guards");

            migrationBuilder.DropTable(
                name: "payment_attempt_items");

            migrationBuilder.DropTable(
                name: "payment_attempts");

            migrationBuilder.DropTable(
                name: "billing_customers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_obligations_id_account_id",
                table: "obligations");
        }
    }
}
