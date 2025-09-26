using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedundantUpscaleQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UpscalingQueueEntries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpscalingQueueEntries",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpscalerProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalingQueueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpscalingQueueEntries_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_UpscalingQueueEntries_UpscalerProfiles_UpscalerProfileId",
                        column: x => x.UpscalerProfileId,
                        principalTable: "UpscalerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQueueEntries_ChapterId",
                table: "UpscalingQueueEntries",
                column: "ChapterId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQueueEntries_UpscalerProfileId",
                table: "UpscalingQueueEntries",
                column: "UpscalerProfileId"
            );
        }
    }
}
