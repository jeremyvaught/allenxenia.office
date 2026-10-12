using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AllenBuilding.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class BuildingKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "help_articles",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    building_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    draft_revision_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    published_revision_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_help_articles", x => x.id);
                    table.ForeignKey(
                        name: "FK_help_articles_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "help_revisions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    article_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    audience = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    editor_id = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    review_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    edited_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_help_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_help_revisions_asp_net_users_editor_id",
                        column: x => x.editor_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_help_revisions_help_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "help_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_help_articles_building_id_state_id",
                table: "help_articles",
                columns: new[] { "building_id", "state", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_help_revisions_article_id_edited_at_id",
                table: "help_revisions",
                columns: new[] { "article_id", "edited_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_help_revisions_editor_id",
                table: "help_revisions",
                column: "editor_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "help_revisions");

            migrationBuilder.DropTable(
                name: "help_articles");
        }
    }
}
