using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class RemoveWornAvatarColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "worn_avatar_id",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "worn_avatar_name",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "worn_avatar_until",
                table: "photos");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "worn_avatar_id",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "worn_avatar_name",
                table: "photos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "worn_avatar_until",
                table: "photos",
                type: "TEXT",
                nullable: true);
        }
    }
}
