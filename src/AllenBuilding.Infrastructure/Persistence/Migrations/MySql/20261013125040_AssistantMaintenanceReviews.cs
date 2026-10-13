using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class AssistantMaintenanceReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_maintenance_reviews",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    conversation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    owner_id = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false, collation: "utf8mb4_bin"),
                    membership_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    membership_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    security_stamp = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_bin"),
                    kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    work_order_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb4_bin"),
                    work_order_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb4_bin"),
                    title = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false, collation: "utf8mb4_bin"),
                    description = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false, collation: "utf8mb4_bin"),
                    location = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false, collation: "utf8mb4_bin"),
                    comment = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false, collation: "utf8mb4_bin"),
                    sources_json = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false, collation: "utf8mb4_bin"),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_bin"),
                    payload_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    state = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    result_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb4_bin"),
                    result_version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb4_bin"),
                    result_status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_maintenance_reviews", x => x.id);
                    table.ForeignKey(
                        name: "FK_assistant_maintenance_reviews_asp_net_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assistant_maintenance_reviews_memberships_membership_id",
                        column: x => x.membership_id,
                        principalTable: "memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assistant_maintenance_reviews_work_orders_result_id",
                        column: x => x.result_id,
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assistant_maintenance_reviews_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_maintenance_reviews_conversation_id",
                table: "assistant_maintenance_reviews",
                column: "conversation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_maintenance_reviews_membership_id_state",
                table: "assistant_maintenance_reviews",
                columns: new[] { "membership_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_maintenance_reviews_owner_id_operation_id",
                table: "assistant_maintenance_reviews",
                columns: new[] { "owner_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_maintenance_reviews_result_id",
                table: "assistant_maintenance_reviews",
                column: "result_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_maintenance_reviews_state_expires_at",
                table: "assistant_maintenance_reviews",
                columns: new[] { "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_maintenance_reviews_work_order_id",
                table: "assistant_maintenance_reviews",
                column: "work_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_maintenance_reviews");
        }
    }
}
