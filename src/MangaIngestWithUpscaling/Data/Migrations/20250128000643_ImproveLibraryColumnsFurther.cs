using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations;

/// <inheritdoc />
public partial class ImproveLibraryColumnsFurther : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
            table: "Libraries"
        );

        migrationBuilder.AlterColumn<int>(
            name: "UpscalerConfigId",
            table: "Libraries",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER"
        );

        migrationBuilder.AddForeignKey(
            name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
            table: "Libraries",
            column: "UpscalerConfigId",
            principalTable: "UpscalerConfigs",
            principalColumn: "Id"
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
            table: "Libraries"
        );

        migrationBuilder.AlterColumn<int>(
            name: "UpscalerConfigId",
            table: "Libraries",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true
        );

        migrationBuilder.AddForeignKey(
            name: "FK_Libraries_UpscalerConfigs_UpscalerConfigId",
            table: "Libraries",
            column: "UpscalerConfigId",
            principalTable: "UpscalerConfigs",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade
        );
    }
}
