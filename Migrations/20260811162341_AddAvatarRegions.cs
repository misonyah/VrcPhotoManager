using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarRegions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "avatar_regions",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    photo_id = table.Column<long>(type: "INTEGER", nullable: false),
                    x = table.Column<int>(type: "INTEGER", nullable: false),
                    y = table.Column<int>(type: "INTEGER", nullable: false),
                    width = table.Column<int>(type: "INTEGER", nullable: false),
                    height = table.Column<int>(type: "INTEGER", nullable: false),
                    avatar_catalog_id = table.Column<string>(type: "TEXT", nullable: true),
                    avatar_display_name = table.Column<string>(type: "TEXT", nullable: true),
                    tagged_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_avatar_regions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_avatar_regions_photo_id",
                table: "avatar_regions",
                column: "photo_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "avatar_regions");
        }
    }
}
