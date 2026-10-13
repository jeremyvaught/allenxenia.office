using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DurableCollectionWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_work",
                columns: table => new
                {
                    attempt_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    api_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    public_origin = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    payment_method_configuration = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    lease_token = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    failures = table.Column<int>(type: "INTEGER", nullable: false),
                    first_failure_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    origin_trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    origin_span_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_work", x => x.attempt_id);
                    table.ForeignKey(
                        name: "FK_collection_work_payment_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "payment_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_collection_work_state_next_attempt_at_created_at_attempt_id",
                table: "collection_work",
                columns: new[] { "state", "next_attempt_at", "created_at", "attempt_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_work");
        }
    }
}
