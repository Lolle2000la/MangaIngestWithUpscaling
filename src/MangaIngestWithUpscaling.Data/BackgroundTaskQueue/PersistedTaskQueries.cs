using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Data.BackgroundTaskQueue;

/// <summary>
/// Queries over the JSON <c>Data</c> payload of <see cref="PersistedTask"/>.
/// SQLite and PostgreSQL use different JSON path syntax and array-expansion functions, so the
/// provider-specific SQL is built in one place here instead of being duplicated at every call site.
/// </summary>
public static class PersistedTaskQueries
{
    /// <summary>
    /// Returns the persisted tasks of the given concrete type that target the given chapter.
    /// </summary>
    public static IQueryable<PersistedTask> ForTaskTypeAndChapter<TTask>(
        ApplicationDbContext context,
        int chapterId,
        IReadOnlyCollection<PersistedTaskStatus>? statuses = null
    )
        where TTask : BaseTask
    {
        bool postgres = IsPostgres(context);

        // SQLite's ->> returns the native JSON type (an integer here), while PostgreSQL's returns
        // text, so the chapter id parameter has to match the provider's representation.
        var parameters = new List<object>
        {
            typeof(TTask).Name,
            postgres ? chapterId.ToString(CultureInfo.InvariantCulture) : chapterId,
        };

        // Derive the placeholder offset from the parameters already collected, so inserting a new
        // filter above cannot silently misalign the status clause's placeholders.
        string statusClause = BuildStatusClause(statuses, parameters, parameters.Count);

        string sql = postgres
            ? """
                SELECT * FROM "PersistedTasks"
                WHERE "Data"->>'$type' = {0}
                  AND "Data"->>'ChapterId' = {1}
                """
            : """
                SELECT * FROM "PersistedTasks"
                WHERE "Data"->>'$.$type' = {0}
                  AND "Data"->>'$.ChapterId' = {1}
                """;

        // FormattableStringFactory keeps every caller-supplied value parameterized while still
        // allowing the SQL text (provider dialect + placeholder count) to be composed at runtime.
        return context.PersistedTasks.FromSql(
            FormattableStringFactory.Create(sql + statusClause, parameters.ToArray())
        );
    }

    /// <summary>
    /// Returns the persisted tasks whose concrete type is one of <paramref name="taskTypes"/> and
    /// that target one of <paramref name="chapterIds"/>.
    /// </summary>
    public static IQueryable<PersistedTask> ForTaskTypesAndChapters(
        ApplicationDbContext context,
        IReadOnlyCollection<int> chapterIds,
        IReadOnlyCollection<string> taskTypes,
        IReadOnlyCollection<PersistedTaskStatus>? statuses = null
    )
    {
        var parameters = new List<object>
        {
            JsonSerializer.Serialize(chapterIds),
            JsonSerializer.Serialize(taskTypes),
        };

        // Derive the placeholder offset from the parameters already collected, so inserting a new
        // filter above cannot silently misalign the status clause's placeholders.
        string statusClause = BuildStatusClause(statuses, parameters, parameters.Count);
        bool postgres = IsPostgres(context);

        string sql = postgres
            ? """
                SELECT * FROM "PersistedTasks"
                WHERE "Data"->>'ChapterId' IN (SELECT jsonb_array_elements_text({0}::jsonb))
                  AND "Data"->>'$type' IN (SELECT jsonb_array_elements_text({1}::jsonb))
                """
            : """
                SELECT * FROM "PersistedTasks"
                WHERE "Data"->>'$.ChapterId' IN (SELECT value FROM json_each({0}))
                  AND "Data"->>'$.$type' IN (SELECT value FROM json_each({1}))
                """;

        return context.PersistedTasks.FromSql(
            FormattableStringFactory.Create(sql + statusClause, parameters.ToArray())
        );
    }

    private static bool IsPostgres(ApplicationDbContext context) =>
        context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
        == true;

    private static string BuildStatusClause(
        IReadOnlyCollection<PersistedTaskStatus>? statuses,
        List<object> parameters,
        int startIndex
    )
    {
        if (statuses is null || statuses.Count == 0)
        {
            return string.Empty;
        }

        var placeholders = new List<string>(statuses.Count);
        int index = startIndex;
        foreach (PersistedTaskStatus status in statuses)
        {
            placeholders.Add($"{{{index++}}}");
            parameters.Add(status.ToString());
        }

        return $" AND \"Status\" IN ({string.Join(", ", placeholders)})";
    }
}
