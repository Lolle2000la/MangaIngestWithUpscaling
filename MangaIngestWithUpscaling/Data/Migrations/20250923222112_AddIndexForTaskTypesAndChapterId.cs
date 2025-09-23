using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MangaIngestWithUpscaling.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexForTaskTypesAndChapterId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Functional indexes on JSON paths used by raw SQL in LibraryIntegrityChecker.
            // Only added for SQLite here, matching current raw SQL syntax (Data->>'$.$type', Data->>'$.ChapterId').
            if (migrationBuilder.ActiveProvider.Contains("Sqlite"))
            {
                migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS IX_PersistedTasks_Type ON PersistedTasks (Data->>'$.$type')");
                migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS IX_PersistedTasks_ChapterId ON PersistedTasks (Data->>'$.ChapterId')");
                migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS IX_PersistedTasks_Type_ChapterId ON PersistedTasks (Data->>'$.$type', Data->>'$.ChapterId')");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Sqlite"))
            {
                migrationBuilder.Sql("DROP INDEX IF EXISTS IX_PersistedTasks_Type_ChapterId");
                migrationBuilder.Sql("DROP INDEX IF EXISTS IX_PersistedTasks_ChapterId");
                migrationBuilder.Sql("DROP INDEX IF EXISTS IX_PersistedTasks_Type");
            }
        }
    }
}
