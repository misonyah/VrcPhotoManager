using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadCropMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "upload_crop_mode",
                table: "photos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "upload_crop_mode",
                table: "photos");
        }
    }
}
