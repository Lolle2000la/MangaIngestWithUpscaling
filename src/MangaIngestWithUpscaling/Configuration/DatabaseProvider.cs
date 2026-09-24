namespace MangaIngestWithUpscaling.Configuration;

/// <summary>
/// The relational database backend the application runs against.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>Embedded SQLite database file (default).</summary>
    Sqlite,

    /// <summary>External PostgreSQL server.</summary>
    Postgres,
}

/// <summary>
/// Parses the configured <c>DatabaseProvider</c> value.
/// </summary>
public static class DatabaseProviderResolver
{
    /// <summary>
    /// Maps a configured provider name to a <see cref="DatabaseProvider" />. An absent or empty
    /// value selects <see cref="DatabaseProvider.Sqlite" /> (the default); an unrecognized non-empty
    /// value throws, because silently guessing the backend would run the application against the
    /// wrong database.
    /// </summary>
    public static DatabaseProvider Resolve(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "" or "sqlite" or "sqlite3" => DatabaseProvider.Sqlite,
            "postgres" or "postgresql" or "npgsql" => DatabaseProvider.Postgres,
            _ => throw new InvalidOperationException(
                $"Unknown DatabaseProvider '{value}'. Valid values are 'Sqlite' and 'Postgres'."
            ),
        };
    }
}
