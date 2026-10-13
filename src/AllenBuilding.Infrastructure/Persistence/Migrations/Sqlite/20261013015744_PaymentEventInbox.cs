using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class PaymentEventInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    provider_account = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    live_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    resource_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    api_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    review_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    received_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_provider_account_live_mode_event_id",
                table: "payment_events",
                columns: new[] { "provider_account", "live_mode", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_state_received_at_id",
                table: "payment_events",
                columns: new[] { "state", "received_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_events");
        }
    }
}
