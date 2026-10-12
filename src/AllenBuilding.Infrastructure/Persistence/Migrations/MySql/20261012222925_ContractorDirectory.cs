using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
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
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    building_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    name = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false, collation: "utf8mb4_bin"),
                    trade = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false, collation: "utf8mb4_bin"),
                    phone = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false, collation: "utf8mb4_bin"),
                    active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin")
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
                })
                .Annotation("MySQL:Charset", "utf8mb4");

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
