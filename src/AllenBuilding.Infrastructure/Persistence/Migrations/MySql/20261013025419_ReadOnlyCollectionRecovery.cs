using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class ReadOnlyCollectionRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "cycle_started_at",
                table: "collection_work",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<bool>(
                name: "read_only_reconciliation",
                table: "collection_work",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Preserve the existing automatic retry age; only a new authorized read starts a new cycle.
            migrationBuilder.Sql("UPDATE collection_work SET cycle_started_at = created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cycle_started_at",
                table: "collection_work");

            migrationBuilder.DropColumn(
                name: "read_only_reconciliation",
                table: "collection_work");
        }
    }
}
