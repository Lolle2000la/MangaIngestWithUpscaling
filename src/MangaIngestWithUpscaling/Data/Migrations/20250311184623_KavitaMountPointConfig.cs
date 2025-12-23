using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class KavitaMountPointConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KavitaConfig_NotUpscaledMountPoint",
                table: "Libraries",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "KavitaConfig_UpscaledMountPoint",
                table: "Libraries",
                type: "TEXT",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KavitaConfig_NotUpscaledMountPoint",
                table: "Libraries"
            );

            migrationBuilder.DropColumn(
                name: "KavitaConfig_UpscaledMountPoint",
                table: "Libraries"
            );
        }
    }
}
