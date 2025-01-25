using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace mangaingestwithupscaling.Migrations
{
    /// <inheritdoc />
    public partial class LibrarySetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Libraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IngestPath = table.Column<string>(type: "TEXT", nullable: false),
                    NotUpscaledLibraryPath = table.Column<string>(type: "TEXT", nullable: false),
                    UpscaledLibraryPath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpscalerConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UpscalerMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    ScalingFactor = table.Column<int>(type: "INTEGER", nullable: false),
                    CompressionFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalerConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryFilterRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    PatternType = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetField = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryFilterRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryFilterRules_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MangaSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrimaryTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MangaSeries_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Chapters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MangaId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    IsUpscaled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpscalerConfigId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chapters_MangaSeries_MangaId",
                        column: x => x.MangaId,
                        principalTable: "MangaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Chapters_UpscalerConfigs_UpscalerConfigId",
                        column: x => x.UpscalerConfigId,
                        principalTable: "UpscalerConfigs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MangaAlternativeTitles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MangaId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaAlternativeTitles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MangaAlternativeTitles_MangaSeries_MangaId",
                        column: x => x.MangaId,
                        principalTable: "MangaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_MangaId",
                table: "Chapters",
                column: "MangaId");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_UpscalerConfigId",
                table: "Chapters",
                column: "UpscalerConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryFilterRules_LibraryId",
                table: "LibraryFilterRules",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_MangaAlternativeTitles_MangaId",
                table: "MangaAlternativeTitles",
                column: "MangaId");

            migrationBuilder.CreateIndex(
                name: "IX_MangaSeries_LibraryId",
                table: "MangaSeries",
                column: "LibraryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Chapters");

            migrationBuilder.DropTable(
                name: "LibraryFilterRules");

            migrationBuilder.DropTable(
                name: "MangaAlternativeTitles");

            migrationBuilder.DropTable(
                name: "UpscalerConfigs");

            migrationBuilder.DropTable(
                name: "MangaSeries");

            migrationBuilder.DropTable(
                name: "Libraries");
        }
    }
}
