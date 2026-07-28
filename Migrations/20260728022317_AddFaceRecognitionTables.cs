using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VrcdnManager.Migrations
{
    /// <inheritdoc />
    public partial class AddFaceRecognitionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "detected_faces",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    photo_id = table.Column<long>(type: "INTEGER", nullable: false),
                    x = table.Column<int>(type: "INTEGER", nullable: false),
                    y = table.Column<int>(type: "INTEGER", nullable: false),
                    width = table.Column<int>(type: "INTEGER", nullable: false),
                    height = table.Column<int>(type: "INTEGER", nullable: false),
                    embedding = table.Column<byte[]>(type: "BLOB", nullable: true),
                    detected_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_detected_faces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "face_labels",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    detected_face_id = table.Column<long>(type: "INTEGER", nullable: false),
                    person_id = table.Column<long>(type: "INTEGER", nullable: true),
                    confidence = table.Column<float>(type: "REAL", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_labels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "person_reference_photos",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    person_id = table.Column<long>(type: "INTEGER", nullable: false),
                    photo_id = table.Column<long>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    added_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_reference_photos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registered_people",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registered_people", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_detected_faces_photo_id",
                table: "detected_faces",
                column: "photo_id");

            migrationBuilder.CreateIndex(
                name: "IX_face_labels_detected_face_id",
                table: "face_labels",
                column: "detected_face_id");

            migrationBuilder.CreateIndex(
                name: "IX_face_labels_person_id",
                table: "face_labels",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "IX_person_reference_photos_person_id_photo_id",
                table: "person_reference_photos",
                columns: new[] { "person_id", "photo_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_registered_people_name",
                table: "registered_people",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "detected_faces");

            migrationBuilder.DropTable(
                name: "face_labels");

            migrationBuilder.DropTable(
                name: "person_reference_photos");

            migrationBuilder.DropTable(
                name: "registered_people");
        }
    }
}
