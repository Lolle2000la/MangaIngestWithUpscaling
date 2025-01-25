using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace mangaingestwithupscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddFilterAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Action",
                table: "LibraryFilterRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Action",
                table: "LibraryFilterRules");
        }
    }
}
