using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_type",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "avatar_type_confidence",
                table: "photos",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avatar_type",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "avatar_type_confidence",
                table: "photos");
        }
    }
}
