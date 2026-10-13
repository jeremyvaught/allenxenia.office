using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.MySql
{
    /// <inheritdoc />
    public partial class VerifiedCashAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "agreement_hash",
                table: "cash_movement_reviews",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "cash_movement_reviews",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agreement_hash",
                table: "cash_movement_reviews");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "cash_movement_reviews");
        }
    }
}
