using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipleIngestPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryIngestPaths",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryIngestPaths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryIngestPaths_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            // Preserve the previously configured single ingest path as the first entry of the new table.
            migrationBuilder.Sql(
                "INSERT INTO \"LibraryIngestPaths\" (\"LibraryId\", \"Path\", \"SortOrder\") "
                    + "SELECT \"Id\", \"IngestPath\", 0 FROM \"Libraries\" "
                    + "WHERE \"IngestPath\" IS NOT NULL AND \"IngestPath\" <> '';"
            );

            migrationBuilder.CreateIndex(
                name: "IX_LibraryIngestPaths_LibraryId_Path",
                table: "LibraryIngestPaths",
                columns: new[] { "LibraryId", "Path" },
                unique: true
            );

            migrationBuilder.DropColumn(name: "IngestPath", table: "Libraries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IngestPath",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            // Restore the first configured ingest path. Additional paths cannot be represented by the
            // old single-column schema and are therefore lost when downgrading.
            migrationBuilder.Sql(
                "UPDATE \"Libraries\" SET \"IngestPath\" = COALESCE(("
                    + "SELECT \"Path\" FROM \"LibraryIngestPaths\" "
                    + "WHERE \"LibraryIngestPaths\".\"LibraryId\" = \"Libraries\".\"Id\" "
                    + "ORDER BY \"SortOrder\", \"Id\" LIMIT 1), '');"
            );

            migrationBuilder.DropTable(name: "LibraryIngestPaths");
        }
    }
}
