namespace MangaIngestWithUpscaling.Tests;

/// <summary>
/// Entity ids for tests that build their own rows.
/// <para>
/// These used to be <c>Random.Shared.Next(...)</c>, which collides: two libraries in one test can draw
/// the same id, and EF then refuses the second one with "another instance with the same key value for
/// {'Id'} is already being tracked". That is a test bug rather than a race, but it only fires on the
/// unlucky draw — the PostgreSQL pass failed once in CI with exactly that message while passing locally.
/// A monotonic counter is unique process-wide (the ids are inserted into a shared container database)
/// and reproducible, so a failure is a real failure.
/// </para>
/// </summary>
internal static class TestIds
{
    private static int _next;

    /// <summary>The next unused id. Unique for the whole test process.</summary>
    public static int Next() => Interlocked.Increment(ref _next);
}
