using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddGamelogInferredPlayers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gamelog_inferred_players",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    photo_id = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gamelog_inferred_players", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gamelog_inferred_players_photo_id",
                table: "gamelog_inferred_players",
                column: "photo_id");

            migrationBuilder.CreateIndex(
                name: "IX_gamelog_inferred_players_user_id",
                table: "gamelog_inferred_players",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gamelog_inferred_players");
        }
    }
}
