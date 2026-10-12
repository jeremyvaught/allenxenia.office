using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class MaintenanceWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_receipts",
                columns: table => new
                {
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    work_order_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    result_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    result_status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_receipts", x => new { x.actor_id, x.operation_id });
                    table.ForeignKey(
                        name: "FK_maintenance_receipts_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_order_history",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    work_order_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    actor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    visibility = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_work_order_history_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_receipts_work_order_id",
                table: "maintenance_receipts",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_order_history_work_order_id_occurred_at_id",
                table: "work_order_history",
                columns: new[] { "work_order_id", "occurred_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_receipts");

            migrationBuilder.DropTable(
                name: "work_order_history");
        }
    }
}
