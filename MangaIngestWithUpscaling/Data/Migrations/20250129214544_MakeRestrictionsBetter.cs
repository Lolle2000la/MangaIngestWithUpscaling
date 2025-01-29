using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class MakeRestrictionsBetter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MangaSeries_PrimaryTitle",
                table: "MangaSeries",
                column: "PrimaryTitle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MangaAlternativeTitles_Title",
                table: "MangaAlternativeTitles",
                column: "Title",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_RelativePath_MangaId",
                table: "Chapters",
                columns: new[] { "RelativePath", "MangaId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MangaSeries_PrimaryTitle",
                table: "MangaSeries");

            migrationBuilder.DropIndex(
                name: "IX_MangaAlternativeTitles_Title",
                table: "MangaAlternativeTitles");

            migrationBuilder.DropIndex(
                name: "IX_Chapters_RelativePath_MangaId",
                table: "Chapters");
        }
    }
}
