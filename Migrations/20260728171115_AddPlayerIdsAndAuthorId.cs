using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcdnManager.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerIdsAndAuthorId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author_id",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "photo_players",
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
                    table.PrimaryKey("PK_photo_players", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_photo_players_photo_id",
                table: "photo_players",
                column: "photo_id");

            migrationBuilder.CreateIndex(
                name: "IX_photo_players_user_id",
                table: "photo_players",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photo_players");

            migrationBuilder.DropColumn(
                name: "author_id",
                table: "photos");
        }
    }
}
