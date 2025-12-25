using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddStripDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StripDetectionMode",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.CreateTable(
                name: "ChapterSplitProcessingStates",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastProcessedDetectorVersion = table.Column<int>(
                        type: "INTEGER",
                        nullable: false
                    ),
                    LastAppliedDetectorVersion = table.Column<int>(
                        type: "INTEGER",
                        nullable: false
                    ),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterSplitProcessingStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChapterSplitProcessingStates_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "StripSplitFindings",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    PageFileName = table.Column<string>(type: "TEXT", nullable: false),
                    SplitJson = table.Column<string>(type: "TEXT", nullable: false),
                    DetectorVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripSplitFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripSplitFindings_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ChapterSplitProcessingStates_ChapterId",
                table: "ChapterSplitProcessingStates",
                column: "ChapterId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_StripSplitFindings_ChapterId",
                table: "StripSplitFindings",
                column: "ChapterId"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ChapterSplitProcessingStates");

            migrationBuilder.DropTable(name: "StripSplitFindings");

            migrationBuilder.DropColumn(name: "StripDetectionMode", table: "Libraries");
        }
    }
}
