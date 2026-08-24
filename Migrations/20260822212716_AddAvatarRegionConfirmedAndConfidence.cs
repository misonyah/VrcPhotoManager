using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarRegionConfirmedAndConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "confidence",
                table: "avatar_regions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "confirmed",
                table: "avatar_regions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confidence",
                table: "avatar_regions");

            migrationBuilder.DropColumn(
                name: "confirmed",
                table: "avatar_regions");
        }
    }
}
