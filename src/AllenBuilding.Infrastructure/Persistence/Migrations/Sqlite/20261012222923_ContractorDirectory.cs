using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ContractorDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contractors",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    building_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    trade = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    phone = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contractors", x => x.id);
                    table.ForeignKey(
                        name: "FK_contractors_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_assigned_contractor_id",
                table: "work_orders",
                column: "assigned_contractor_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_assigned_manager_id",
                table: "work_orders",
                column: "assigned_manager_id");

            migrationBuilder.CreateIndex(
                name: "IX_contractors_building_id_active_name_id",
                table: "contractors",
                columns: new[] { "building_id", "active", "name", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_work_orders_asp_net_users_assigned_manager_id",
                table: "work_orders",
                column: "assigned_manager_id",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_work_orders_contractors_assigned_contractor_id",
                table: "work_orders",
                column: "assigned_contractor_id",
                principalTable: "contractors",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_work_orders_asp_net_users_assigned_manager_id",
                table: "work_orders");

            migrationBuilder.DropForeignKey(
                name: "FK_work_orders_contractors_assigned_contractor_id",
                table: "work_orders");

            migrationBuilder.DropTable(
                name: "contractors");

            migrationBuilder.DropIndex(
                name: "IX_work_orders_assigned_contractor_id",
                table: "work_orders");

            migrationBuilder.DropIndex(
                name: "IX_work_orders_assigned_manager_id",
                table: "work_orders");
        }
    }
}
