using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class LocalObligationCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "obligation_credits",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    account_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    obligation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    reviewed_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    actor_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    amount_minor_units = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false, collation: "utf8mb4_bin"),
                    reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false, collation: "utf8mb4_bin"),
                    recorded_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_obligation_credits", x => x.id);
                    table.CheckConstraint("CK_credit_amount", "amount_minor_units > 0 AND amount_minor_units <= 99999999 AND currency = 'USD'");
                    table.ForeignKey(
                        name: "FK_obligation_credits_asp_net_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_obligation_credits_obligations_obligation_id_account_id",
                        columns: x => new { x.obligation_id, x.account_id },
                        principalTable: "obligations",
                        principalColumns: new[] { "id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_actor_id_operation_id",
                table: "obligation_credits",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_obligation_id_account_id",
                table: "obligation_credits",
                columns: new[] { "obligation_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_obligation_id_recorded_at_id",
                table: "obligation_credits",
                columns: new[] { "obligation_id", "recorded_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "obligation_credits");
        }
    }
}
