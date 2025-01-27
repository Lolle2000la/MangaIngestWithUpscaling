using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class ImproveLibraryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UpscaledLibraryPath",
                table: "Libraries",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "UpscalerConfigId",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_UpscalerConfigId",
                table: "Libraries",
                column: "UpscalerConfigId");

            migrationBuilder.AddForeignKey(
                name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
                table: "Libraries",
                column: "UpscalerConfigId",
                principalTable: "UpscalerConfigs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
                table: "Libraries");

            migrationBuilder.DropIndex(
                name: "IX_Libraries_UpscalerConfigId",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "UpscalerConfigId",
                table: "Libraries");

            migrationBuilder.AlterColumn<string>(
                name: "UpscaledLibraryPath",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
