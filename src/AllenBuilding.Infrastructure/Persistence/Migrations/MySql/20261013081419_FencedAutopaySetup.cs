using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class FencedAutopaySetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_autopay_work_state_created_at_id",
                table: "autopay_work");

            migrationBuilder.AddColumn<string>(
                name: "api_version",
                table: "autopay_work",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "autopay_work",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "deadline_at",
                table: "autopay_work",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "dispatched_at",
                table: "autopay_work",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failures",
                table: "autopay_work",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "autopay_work",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "lease_token",
                table: "autopay_work",
                type: "varchar(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "00000000-0000-0000-0000-000000000000",
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "autopay_work",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "autopay_work",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "payment_method_configuration",
                table: "autopay_work",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "public_origin",
                table: "autopay_work",
                type: "varchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "",
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "version",
                table: "autopay_work",
                type: "varchar(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "00000000-0000-0000-0000-000000000000",
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<DateTime>(
                name: "setup_expires_at",
                table: "autopay_authorizations",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "setup_session_id",
                table: "autopay_authorizations",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "setup_state",
                table: "autopay_authorizations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            // Earlier intentions did not freeze provider request settings. Preserve identities/history and require investigation rather than inventing a request body.
            migrationBuilder.Sql("UPDATE autopay_work SET next_attempt_at = created_at, deadline_at = DATE_ADD(created_at, INTERVAL 2 DAY), version = UUID(), last_error = 'OriginalProfileMissing', state = 'NeedsReview' WHERE state NOT IN ('Completed', 'Suppressed')");
            migrationBuilder.Sql("UPDATE autopay_authorizations SET state = 'NeedsReview', version = UUID() WHERE state IN ('AwaitingMethod', 'AwaitingProvider') AND EXISTS (SELECT 1 FROM autopay_work w WHERE w.authorization_id = autopay_authorizations.id AND w.last_error = 'OriginalProfileMissing')");

            migrationBuilder.CreateIndex(
                name: "IX_autopay_work_stage_state_next_attempt_at_created_at_id",
                table: "autopay_work",
                columns: new[] { "stage", "state", "next_attempt_at", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_autopay_work_stage_state_next_attempt_at_created_at_id",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "api_version",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "deadline_at",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "dispatched_at",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "failures",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "payment_method_configuration",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "public_origin",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "version",
                table: "autopay_work");

            migrationBuilder.DropColumn(
                name: "setup_expires_at",
                table: "autopay_authorizations");

            migrationBuilder.DropColumn(
                name: "setup_session_id",
                table: "autopay_authorizations");

            migrationBuilder.DropColumn(
                name: "setup_state",
                table: "autopay_authorizations");

            migrationBuilder.CreateIndex(
                name: "IX_autopay_work_state_created_at_id",
                table: "autopay_work",
                columns: new[] { "state", "created_at", "id" });
        }
    }
}
