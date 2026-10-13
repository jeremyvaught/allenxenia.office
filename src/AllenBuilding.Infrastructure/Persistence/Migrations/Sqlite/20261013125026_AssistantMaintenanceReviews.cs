using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
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
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    conversation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    membership_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    membership_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    security_stamp = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    work_order_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    work_order_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    location = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    comment = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    sources_json = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    result_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    result_version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    result_status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
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
                });

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
