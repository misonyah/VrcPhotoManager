using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suggestion_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    detected_face_id = table.Column<long>(type: "INTEGER", nullable: false),
                    suggested_person_id = table.Column<long>(type: "INTEGER", nullable: false),
                    combined_score = table.Column<float>(type: "REAL", nullable: false),
                    face_similarity_score = table.Column<float>(type: "REAL", nullable: false),
                    avatar_affinity_boost = table.Column<float>(type: "REAL", nullable: false),
                    co_occurrence_boost = table.Column<float>(type: "REAL", nullable: false),
                    tier = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    outcome_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suggestion_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_suggestion_logs_detected_face_id",
                table: "suggestion_logs",
                column: "detected_face_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suggestion_logs");
        }
    }
}
