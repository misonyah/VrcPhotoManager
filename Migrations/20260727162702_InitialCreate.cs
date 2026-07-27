using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcdnManager.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "photos",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    local_path = table.Column<string>(type: "TEXT", nullable: false),
                    file_size = table.Column<long>(type: "INTEGER", nullable: false),
                    mtime = table.Column<double>(type: "REAL", nullable: false),
                    thumbnail = table.Column<byte[]>(type: "BLOB", nullable: true),
                    rating = table.Column<string>(type: "TEXT", nullable: true),
                    selected = table.Column<bool>(type: "INTEGER", nullable: false),
                    remote_status = table.Column<string>(type: "TEXT", nullable: false),
                    remote_url = table.Column<string>(type: "TEXT", nullable: true),
                    remote_id = table.Column<string>(type: "TEXT", nullable: true),
                    uploaded_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_photos_local_path",
                table: "photos",
                column: "local_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_photos_remote_status",
                table: "photos",
                column: "remote_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photos");

            migrationBuilder.DropTable(
                name: "settings");
        }
    }
}
