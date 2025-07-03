using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddUpscalerProfilePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UpscalerProfilePreferenceId",
                table: "MangaSeries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MangaSeries_UpscalerProfilePreferenceId",
                table: "MangaSeries",
                column: "UpscalerProfilePreferenceId");

            migrationBuilder.AddForeignKey(
                name: "FK_MangaSeries_UpscalerProfiles_UpscalerProfilePreferenceId",
                table: "MangaSeries",
                column: "UpscalerProfilePreferenceId",
                principalTable: "UpscalerProfiles",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MangaSeries_UpscalerProfiles_UpscalerProfilePreferenceId",
                table: "MangaSeries");

            migrationBuilder.DropIndex(
                name: "IX_MangaSeries_UpscalerProfilePreferenceId",
                table: "MangaSeries");

            migrationBuilder.DropColumn(
                name: "UpscalerProfilePreferenceId",
                table: "MangaSeries");
        }
    }
}
