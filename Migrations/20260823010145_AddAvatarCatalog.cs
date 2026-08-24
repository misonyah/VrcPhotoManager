using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "avatar_catalog_id",
                table: "photos",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "avatar_catalog_id",
                table: "avatar_regions",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "avatar_catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    trained_catalog_id = table.Column<string>(type: "TEXT", nullable: true),
                    display_name = table.Column<string>(type: "TEXT", nullable: true),
                    booth_product = table.Column<string>(type: "TEXT", nullable: true),
                    gumroad_user = table.Column<string>(type: "TEXT", nullable: true),
                    gumroad_product = table.Column<string>(type: "TEXT", nullable: true),
                    jinxxy_user = table.Column<string>(type: "TEXT", nullable: true),
                    jinxxy_product = table.Column<string>(type: "TEXT", nullable: true),
                    parent_item_id = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_avatar_catalog", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_avatar_catalog_booth_product",
                table: "avatar_catalog",
                column: "booth_product",
                unique: true,
                filter: "booth_product IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_catalog_gumroad_user_gumroad_product",
                table: "avatar_catalog",
                columns: new[] { "gumroad_user", "gumroad_product" },
                unique: true,
                filter: "gumroad_user IS NOT NULL AND gumroad_product IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_catalog_jinxxy_user_jinxxy_product",
                table: "avatar_catalog",
                columns: new[] { "jinxxy_user", "jinxxy_product" },
                unique: true,
                filter: "jinxxy_user IS NOT NULL AND jinxxy_product IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_catalog_trained_catalog_id",
                table: "avatar_catalog",
                column: "trained_catalog_id",
                unique: true,
                filter: "trained_catalog_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "avatar_catalog");

            migrationBuilder.AlterColumn<string>(
                name: "avatar_catalog_id",
                table: "photos",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "avatar_catalog_id",
                table: "avatar_regions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
