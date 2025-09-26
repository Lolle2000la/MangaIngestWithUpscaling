using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class MakeAlternativeTitlesKeyNatural : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MangaAlternativeTitles",
                table: "MangaAlternativeTitles"
            );

            migrationBuilder.DropIndex(
                name: "IX_MangaAlternativeTitles_MangaId",
                table: "MangaAlternativeTitles"
            );

            migrationBuilder.DropColumn(name: "Id", table: "MangaAlternativeTitles");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MangaAlternativeTitles",
                table: "MangaAlternativeTitles",
                columns: new[] { "MangaId", "Title" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MangaAlternativeTitles",
                table: "MangaAlternativeTitles"
            );

            migrationBuilder
                .AddColumn<int>(
                    name: "Id",
                    table: "MangaAlternativeTitles",
                    type: "INTEGER",
                    nullable: false,
                    defaultValue: 0
                )
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MangaAlternativeTitles",
                table: "MangaAlternativeTitles",
                column: "Id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MangaAlternativeTitles_MangaId",
                table: "MangaAlternativeTitles",
                column: "MangaId"
            );
        }
    }
}
