using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace mangaingestwithupscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddUpscalingQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpscalingQueueEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpscalerConfigId = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpscalingQueueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpscalingQueueEntries_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpscalingQueueEntries_UpscalerConfigs_UpscalerConfigId",
                        column: x => x.UpscalerConfigId,
                        principalTable: "UpscalerConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQueueEntries_ChapterId",
                table: "UpscalingQueueEntries",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_UpscalingQueueEntries_UpscalerConfigId",
                table: "UpscalingQueueEntries",
                column: "UpscalerConfigId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpscalingQueueEntries");
        }
    }
}
