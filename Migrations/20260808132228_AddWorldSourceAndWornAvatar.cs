using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldSourceAndWornAvatar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "world_name_inferred",
                table: "photos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "world_name_inferred",
                table: "photos");

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
    }
}
