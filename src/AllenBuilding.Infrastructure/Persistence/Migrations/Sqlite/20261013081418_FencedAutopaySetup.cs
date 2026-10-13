using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
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
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "autopay_work",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "deadline_at",
                table: "autopay_work",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "dispatched_at",
                table: "autopay_work",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failures",
                table: "autopay_work",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "autopay_work",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lease_token",
                table: "autopay_work",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "00000000-0000-0000-0000-000000000000");

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "autopay_work",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "autopay_work",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "payment_method_configuration",
                table: "autopay_work",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "public_origin",
                table: "autopay_work",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "version",
                table: "autopay_work",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "00000000-0000-0000-0000-000000000000");

            migrationBuilder.AddColumn<DateTime>(
                name: "setup_expires_at",
                table: "autopay_authorizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "setup_session_id",
                table: "autopay_authorizations",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "setup_state",
                table: "autopay_authorizations",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            // Earlier intentions did not freeze provider request settings. Preserve identities/history and require investigation rather than inventing a request body.
            migrationBuilder.Sql("UPDATE autopay_work SET next_attempt_at = created_at, deadline_at = strftime('%Y-%m-%d %H:%M:%f', created_at, '+2 days'), version = lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))), last_error = 'OriginalProfileMissing', state = 'NeedsReview' WHERE state NOT IN ('Completed', 'Suppressed')");
            migrationBuilder.Sql("UPDATE autopay_authorizations SET state = 'NeedsReview', version = lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))) WHERE state IN ('AwaitingMethod', 'AwaitingProvider') AND EXISTS (SELECT 1 FROM autopay_work w WHERE w.authorization_id = autopay_authorizations.id AND w.last_error = 'OriginalProfileMissing')");

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
