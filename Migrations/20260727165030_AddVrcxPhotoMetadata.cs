using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddVrcxPhotoMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author_display_name",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "metadata_scanned",
                table: "photos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "player_names",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "world_name",
                table: "photos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "author_display_name",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "metadata_scanned",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "player_names",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "world_name",
                table: "photos");
        }
    }
}
