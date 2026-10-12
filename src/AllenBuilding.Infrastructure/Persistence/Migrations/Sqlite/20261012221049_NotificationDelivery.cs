using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NotificationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "account_messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "building_id",
                table: "account_messages",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cycle_attempts",
                table: "account_messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "delivered_at",
                table: "account_messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "account_messages",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lease_token",
                table: "account_messages",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "00000000-0000-0000-0000-000000000000");

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "account_messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "account_messages",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "account_messages",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Queued");

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "account_audit",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            // Existing encrypted messages remain valid and become due at their original creation time.
            migrationBuilder.Sql("UPDATE account_messages SET next_attempt_at = created_at");

            migrationBuilder.CreateIndex(
                name: "IX_account_messages_building_id",
                table: "account_messages",
                column: "building_id");

            migrationBuilder.CreateIndex(
                name: "IX_account_messages_state_next_attempt_at_created_at_id",
                table: "account_messages",
                columns: new[] { "state", "next_attempt_at", "created_at", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_account_messages_buildings_building_id",
                table: "account_messages",
                column: "building_id",
                principalTable: "buildings",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_account_messages_buildings_building_id",
                table: "account_messages");

            migrationBuilder.DropIndex(
                name: "IX_account_messages_building_id",
                table: "account_messages");

            migrationBuilder.DropIndex(
                name: "IX_account_messages_state_next_attempt_at_created_at_id",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "building_id",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "cycle_attempts",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "state",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "account_audit");
        }
    }
}
