using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddImageMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "file_hash",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "height",
                table: "photos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width",
                table: "photos",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "file_hash",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "height",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "width",
                table: "photos");
        }
    }
}
