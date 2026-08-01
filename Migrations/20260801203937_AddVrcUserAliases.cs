using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcPhotoManager.Migrations
{
    /// <inheritdoc />
    public partial class AddVrcUserAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vrc_user_aliases",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    alias = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    added_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vrc_user_aliases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vrc_user_aliases_user_id",
                table: "vrc_user_aliases",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_vrc_user_aliases_user_id_alias",
                table: "vrc_user_aliases",
                columns: new[] { "user_id", "alias" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vrc_user_aliases");
        }
    }
}
