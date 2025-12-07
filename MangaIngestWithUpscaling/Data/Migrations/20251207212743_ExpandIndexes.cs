using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations;

/// <inheritdoc />
public partial class ExpandIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_UpscalerProfiles_Deleted",
            table: "UpscalerProfiles",
            column: "Deleted"
        );

        migrationBuilder.CreateIndex(
            name: "IX_UpscalerProfiles_Id_Deleted",
            table: "UpscalerProfiles",
            columns: new[] { "Id", "Deleted" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_PersistedTasks_Order",
            table: "PersistedTasks",
            column: "Order"
        );

        migrationBuilder.CreateIndex(
            name: "IX_MergedChapterInfos_MergedChapterNumber",
            table: "MergedChapterInfos",
            column: "MergedChapterNumber"
        );

        migrationBuilder.CreateIndex(
            name: "IX_Chapters_IsUpscaled",
            table: "Chapters",
            column: "IsUpscaled"
        );

        migrationBuilder.CreateIndex(
            name: "IX_ApiKeys_Key",
            table: "ApiKeys",
            column: "Key",
            unique: true
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_UpscalerProfiles_Deleted", table: "UpscalerProfiles");

        migrationBuilder.DropIndex(
            name: "IX_UpscalerProfiles_Id_Deleted",
            table: "UpscalerProfiles"
        );

        migrationBuilder.DropIndex(name: "IX_PersistedTasks_Order", table: "PersistedTasks");

        migrationBuilder.DropIndex(
            name: "IX_MergedChapterInfos_MergedChapterNumber",
            table: "MergedChapterInfos"
        );

        migrationBuilder.DropIndex(name: "IX_Chapters_IsUpscaled", table: "Chapters");

        migrationBuilder.DropIndex(name: "IX_ApiKeys_Key", table: "ApiKeys");
    }
}
