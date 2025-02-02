using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class RenameUpscalerConfigToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chapters_UpscalerConfigs_UpscalerConfigId",
                table: "Chapters");

            migrationBuilder.DropForeignKey(
                name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
                table: "Libraries");

            migrationBuilder.DropForeignKey(
                name: "FK_UpscalingQueueEntries_UpscalerConfigs_UpscalerConfigId",
                table: "UpscalingQueueEntries");

            // Rename the existing table instead of dropping it
            migrationBuilder.RenameTable(
                name: "UpscalerConfigs",
                newName: "UpscalerProfiles");

            // Rename columns in dependent tables
            migrationBuilder.RenameColumn(
                name: "UpscalerConfigId",
                table: "UpscalingQueueEntries",
                newName: "UpscalerProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_UpscalingQueueEntries_UpscalerConfigId",
                table: "UpscalingQueueEntries",
                newName: "IX_UpscalingQueueEntries_UpscalerProfileId");

            migrationBuilder.RenameColumn(
                name: "UpscalerConfigId",
                table: "Libraries",
                newName: "UpscalerProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_Libraries_UpscalerConfigId",
                table: "Libraries",
                newName: "IX_Libraries_UpscalerProfileId");

            migrationBuilder.RenameColumn(
                name: "UpscalerConfigId",
                table: "Chapters",
                newName: "UpscalerProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_Chapters_UpscalerConfigId",
                table: "Chapters",
                newName: "IX_Chapters_UpscalerProfileId");

            // Recreate foreign keys pointing to the renamed table
            migrationBuilder.AddForeignKey(
                name: "FK_Chapters_UpscalerProfiles_UpscalerProfileId",
                table: "Chapters",
                column: "UpscalerProfileId",
                principalTable: "UpscalerProfiles",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Libraries_UpscalerProfiles_UpscalerProfileId",
                table: "Libraries",
                column: "UpscalerProfileId",
                principalTable: "UpscalerProfiles",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UpscalingQueueEntries_UpscalerProfiles_UpscalerProfileId",
                table: "UpscalingQueueEntries",
                column: "UpscalerProfileId",
                principalTable: "UpscalerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chapters_UpscalerProfiles_UpscalerProfileId",
                table: "Chapters");

            migrationBuilder.DropForeignKey(
                name: "FK_Libraries_UpscalerProfiles_UpscalerProfileId",
                table: "Libraries");

            migrationBuilder.DropForeignKey(
                name: "FK_UpscalingQueueEntries_UpscalerProfiles_UpscalerProfileId",
                table: "UpscalingQueueEntries");

            // Rename the table back to the original name
            migrationBuilder.RenameTable(
                name: "UpscalerProfiles",
                newName: "UpscalerConfigs");

            // Revert column names in dependent tables
            migrationBuilder.RenameColumn(
                name: "UpscalerProfileId",
                table: "UpscalingQueueEntries",
                newName: "UpscalerConfigId");

            migrationBuilder.RenameIndex(
                name: "IX_UpscalingQueueEntries_UpscalerProfileId",
                table: "UpscalingQueueEntries",
                newName: "IX_UpscalingQueueEntries_UpscalerConfigId");

            migrationBuilder.RenameColumn(
                name: "UpscalerProfileId",
                table: "Libraries",
                newName: "UpscalerConfigId");

            migrationBuilder.RenameIndex(
                name: "IX_Libraries_UpscalerProfileId",
                table: "Libraries",
                newName: "IX_Libraries_UpscalerConfigId");

            migrationBuilder.RenameColumn(
                name: "UpscalerProfileId",
                table: "Chapters",
                newName: "UpscalerConfigId");

            migrationBuilder.RenameIndex(
                name: "IX_Chapters_UpscalerProfileId",
                table: "Chapters",
                newName: "IX_Chapters_UpscalerConfigId");

            // Re-add original foreign keys
            migrationBuilder.AddForeignKey(
                name: "FK_Chapters_UpscalerConfigs_UpscalerConfigId",
                table: "Chapters",
                column: "UpscalerConfigId",
                principalTable: "UpscalerConfigs",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
                table: "Libraries",
                column: "UpscalerConfigId",
                principalTable: "UpscalerConfigs",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UpscalingQueueEntries_UpscalerConfigs_UpscalerConfigId",
                table: "UpscalingQueueEntries",
                column: "UpscalerConfigId",
                principalTable: "UpscalerConfigs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}