using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var defaultDate = DateTime.UtcNow;

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "UpscalerProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "UpscalerProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "MangaSeries",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "MangaSeries",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "MangaAlternativeTitles",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "Chapters",
                type: "TEXT",
                nullable: false,
                defaultValue: defaultDate);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "UpscalerProfiles");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "UpscalerProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "MangaSeries");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "MangaSeries");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "MangaAlternativeTitles");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "Chapters");
        }
    }
}
