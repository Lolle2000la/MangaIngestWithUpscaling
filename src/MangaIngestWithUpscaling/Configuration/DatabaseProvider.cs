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
