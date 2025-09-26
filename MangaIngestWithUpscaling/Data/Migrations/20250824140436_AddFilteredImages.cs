using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddFilteredImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilteredImages",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", nullable: false),
                    ThumbnailBase64 = table.Column<string>(type: "TEXT", nullable: true),
                    MimeType = table.Column<string>(type: "TEXT", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DateAdded = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastMatchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilteredImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilteredImages_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_ContentHash",
                table: "FilteredImages",
                column: "ContentHash"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_DateAdded",
                table: "FilteredImages",
                column: "DateAdded"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_LibraryId",
                table: "FilteredImages",
                column: "LibraryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_LibraryId_OriginalFileName",
                table: "FilteredImages",
                columns: new[] { "LibraryId", "OriginalFileName" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_FilteredImages_OccurrenceCount",
                table: "FilteredImages",
                column: "OccurrenceCount"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "FilteredImages");
        }
    }
}
