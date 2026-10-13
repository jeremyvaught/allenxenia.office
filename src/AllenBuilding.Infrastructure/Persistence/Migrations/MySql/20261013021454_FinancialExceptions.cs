using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class FinancialExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_exceptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    attempt_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    failure = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    owner_user_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: true, collation: "utf8mb4_bin"),
                    last_verified_state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_bin"),
                    last_verified_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    first_seen_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_exceptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_financial_exceptions_asp_net_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_financial_exceptions_payment_attempts_attempt_id_account_id",
                        columns: x => new { x.attempt_id, x.account_id },
                        principalTable: "payment_attempts",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "financial_exception_history",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    exception_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    action = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    actor_user_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: true, collation: "utf8mb4_bin"),
                    reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_bin"),
                    occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_exception_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_financial_exception_history_asp_net_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_financial_exception_history_financial_exceptions_exception_id",
                        column: x => x.exception_id,
                        principalTable: "financial_exceptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_financial_exception_history_actor_user_id",
                table: "financial_exception_history",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_financial_exception_history_exception_id_occurred_at_id",
                table: "financial_exception_history",
                columns: new[] { "exception_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_financial_exceptions_account_id_state_last_seen_at",
                table: "financial_exceptions",
                columns: new[] { "account_id", "state", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "IX_financial_exceptions_attempt_id",
                table: "financial_exceptions",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_financial_exceptions_attempt_id_account_id",
                table: "financial_exceptions",
                columns: new[] { "attempt_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_financial_exceptions_owner_user_id",
                table: "financial_exceptions",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_exception_history");

            migrationBuilder.DropTable(
                name: "financial_exceptions");
        }
    }
}
