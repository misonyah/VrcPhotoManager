using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryAndPhotoLibraryId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "library_id",
                table: "photos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "libraries",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    local_path = table.Column<string>(type: "TEXT", nullable: true),
                    discord_guild_id = table.Column<string>(type: "TEXT", nullable: true),
                    discord_channel_id = table.Column<string>(type: "TEXT", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_synced_message_id = table.Column<string>(type: "TEXT", nullable: true),
                    auto_download_originals = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_libraries", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "libraries");

            migrationBuilder.DropColumn(
                name: "library_id",
                table: "photos");
        }
    }
}
