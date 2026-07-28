using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonVrcIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_registered_people_name",
                table: "registered_people");

            migrationBuilder.AddColumn<byte[]>(
                name: "vrc_profile_thumbnail",
                table: "registered_people",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "vrc_profile_thumbnail_fetched_at",
                table: "registered_people",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vrc_user_id",
                table: "registered_people",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_registered_people_vrc_user_id",
                table: "registered_people",
                column: "vrc_user_id",
                unique: true,
                filter: "vrc_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_registered_people_vrc_user_id",
                table: "registered_people");

            migrationBuilder.DropColumn(
                name: "vrc_profile_thumbnail",
                table: "registered_people");

            migrationBuilder.DropColumn(
                name: "vrc_profile_thumbnail_fetched_at",
                table: "registered_people");

            migrationBuilder.DropColumn(
                name: "vrc_user_id",
                table: "registered_people");

            migrationBuilder.CreateIndex(
                name: "IX_registered_people_name",
                table: "registered_people",
                column: "name",
                unique: true);
        }
    }
}
