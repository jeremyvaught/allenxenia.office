using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class PaymentEventProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_events_state_received_at_id",
                table: "payment_events");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "payment_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "failures",
                table: "payment_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "lease_token",
                table: "payment_events",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "00000000-0000-0000-0000-000000000000");

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "payment_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "payment_events",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            // Preserve the delivery schedule for authenticated rows that predate worker leases.
            migrationBuilder.Sql("UPDATE payment_events SET next_attempt_at = received_at");

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_state_next_attempt_at_received_at_id",
                table: "payment_events",
                columns: new[] { "state", "next_attempt_at", "received_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_events_state_next_attempt_at_received_at_id",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "failures",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "payment_events");

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_state_received_at_id",
                table: "payment_events",
                columns: new[] { "state", "received_at", "id" });
        }
    }
}
