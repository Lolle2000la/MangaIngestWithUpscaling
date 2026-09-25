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
        if (TryResolve(value, out DatabaseProvider provider, out string? error))
        {
            return provider;
        }

        throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Attempts to map a configured provider name to a <see cref="DatabaseProvider" />. An absent or
    /// empty value selects <see cref="DatabaseProvider.Sqlite" /> (the default). Returns
    /// <see langword="false" /> and sets <paramref name="error" /> for an unrecognized value instead
    /// of throwing, so callers that report errors can share the same mapping.
    /// </summary>
    public static bool TryResolve(string? value, out DatabaseProvider provider, out string? error)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (normalized)
        {
            case "":
            case "sqlite":
            case "sqlite3":
                provider = DatabaseProvider.Sqlite;
                error = null;
                return true;
            case "postgres":
            case "postgresql":
            case "npgsql":
                provider = DatabaseProvider.Postgres;
                error = null;
                return true;
            default:
                provider = DatabaseProvider.Sqlite;
                error =
                    $"Unknown DatabaseProvider '{value}'. Valid values are 'Sqlite' and 'Postgres'.";
                return false;
        }
    }
}
