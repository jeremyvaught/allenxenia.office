using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class NotificationTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin_operation_id",
                table: "account_messages",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "origin_span_id",
                table: "account_messages",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<string>(
                name: "origin_trace_id",
                table: "account_messages",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_bin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "origin_operation_id",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "origin_span_id",
                table: "account_messages");

            migrationBuilder.DropColumn(
                name: "origin_trace_id",
                table: "account_messages");
        }
    }
}
