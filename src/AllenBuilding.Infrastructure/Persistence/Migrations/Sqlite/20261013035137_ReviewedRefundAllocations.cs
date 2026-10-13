using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ReviewedRefundAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_obligation_credits_actor_id_operation_id",
                table: "obligation_credits");

            migrationBuilder.AddColumn<string>(
                name: "source_movement_id",
                table: "obligation_credits",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_id",
                table: "cash_movement_reviews",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "applied_at",
                table: "cash_movement_reviews",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disposition",
                table: "cash_movement_reviews",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "operation_id",
                table: "cash_movement_reviews",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "cash_movement_reviews",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_payment_allocations_payment_id_obligation_id_account_id",
                table: "payment_allocations",
                columns: new[] { "payment_id", "obligation_id", "account_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_cash_movements_id_capture_id_account_id",
                table: "cash_movements",
                columns: new[] { "id", "capture_id", "account_id" });

            migrationBuilder.CreateTable(
                name: "refund_allocations",
                columns: table => new
                {
                    movement_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    obligation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    capture_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    account_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    reviewed_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    signed_minor_units = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_allocations", x => new { x.movement_id, x.obligation_id });
                    table.CheckConstraint("CK_refund_allocation_amount", "signed_minor_units < 0 AND signed_minor_units >= -99999999");
                    table.ForeignKey(
                        name: "FK_refund_allocations_cash_movements_movement_id_capture_id_account_id",
                        columns: x => new { x.movement_id, x.capture_id, x.account_id },
                        principalTable: "cash_movements",
                        principalColumns: new[] { "id", "capture_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refund_allocations_payment_allocations_capture_id_obligation_id_account_id",
                        columns: x => new { x.capture_id, x.obligation_id, x.account_id },
                        principalTable: "payment_allocations",
                        principalColumns: new[] { "payment_id", "obligation_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_actor_id_operation_id_obligation_id",
                table: "obligation_credits",
                columns: new[] { "actor_id", "operation_id", "obligation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_source_movement_id_account_id",
                table: "obligation_credits",
                columns: new[] { "source_movement_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_source_movement_id_obligation_id",
                table: "obligation_credits",
                columns: new[] { "source_movement_id", "obligation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cash_movement_reviews_actor_id",
                table: "cash_movement_reviews",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_allocations_account_id_obligation_id",
                table: "refund_allocations",
                columns: new[] { "account_id", "obligation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refund_allocations_capture_id_obligation_id",
                table: "refund_allocations",
                columns: new[] { "capture_id", "obligation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refund_allocations_capture_id_obligation_id_account_id",
                table: "refund_allocations",
                columns: new[] { "capture_id", "obligation_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refund_allocations_movement_id_capture_id_account_id",
                table: "refund_allocations",
                columns: new[] { "movement_id", "capture_id", "account_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_cash_movement_reviews_asp_net_users_actor_id",
                table: "cash_movement_reviews",
                column: "actor_id",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_obligation_credits_cash_movements_source_movement_id_account_id",
                table: "obligation_credits",
                columns: new[] { "source_movement_id", "account_id" },
                principalTable: "cash_movements",
                principalColumns: new[] { "id", "account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_obligation_credits_refund_allocations_source_movement_id_obligation_id",
                table: "obligation_credits",
                columns: new[] { "source_movement_id", "obligation_id" },
                principalTable: "refund_allocations",
                principalColumns: new[] { "movement_id", "obligation_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cash_movement_reviews_asp_net_users_actor_id",
                table: "cash_movement_reviews");

            migrationBuilder.DropForeignKey(
                name: "FK_obligation_credits_cash_movements_source_movement_id_account_id",
                table: "obligation_credits");

            migrationBuilder.DropForeignKey(
                name: "FK_obligation_credits_refund_allocations_source_movement_id_obligation_id",
                table: "obligation_credits");

            migrationBuilder.DropTable(
                name: "refund_allocations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_payment_allocations_payment_id_obligation_id_account_id",
                table: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "IX_obligation_credits_actor_id_operation_id_obligation_id",
                table: "obligation_credits");

            migrationBuilder.DropIndex(
                name: "IX_obligation_credits_source_movement_id_account_id",
                table: "obligation_credits");

            migrationBuilder.DropIndex(
                name: "IX_obligation_credits_source_movement_id_obligation_id",
                table: "obligation_credits");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_cash_movements_id_capture_id_account_id",
                table: "cash_movements");

            migrationBuilder.DropIndex(
                name: "IX_cash_movement_reviews_actor_id",
                table: "cash_movement_reviews");

            migrationBuilder.DropColumn(
                name: "source_movement_id",
                table: "obligation_credits");

            migrationBuilder.DropColumn(
                name: "actor_id",
                table: "cash_movement_reviews");

            migrationBuilder.DropColumn(
                name: "applied_at",
                table: "cash_movement_reviews");

            migrationBuilder.DropColumn(
                name: "disposition",
                table: "cash_movement_reviews");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "cash_movement_reviews");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "cash_movement_reviews");

            migrationBuilder.CreateIndex(
                name: "IX_obligation_credits_actor_id_operation_id",
                table: "obligation_credits",
                columns: new[] { "actor_id", "operation_id" },
                unique: true);
        }
    }
}
