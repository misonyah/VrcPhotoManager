using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordPhotoFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "local_path",
                table: "photos",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<DateTime>(
                name: "last_accessed_at",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_source_id",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_source_url",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_photos_remote_source_id",
                table: "photos",
                column: "remote_source_id",
                unique: true,
                filter: "remote_source_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_photos_remote_source_id",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "last_accessed_at",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "remote_source_id",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "remote_source_url",
                table: "photos");

            migrationBuilder.AlterColumn<string>(
                name: "local_path",
                table: "photos",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
