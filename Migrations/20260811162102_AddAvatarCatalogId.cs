using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarCatalogId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_catalog_id",
                table: "photos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avatar_catalog_id",
                table: "photos");
        }
    }
}
