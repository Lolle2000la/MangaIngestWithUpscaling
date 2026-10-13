namespace MangaIngestWithUpscaling.Shared.Helpers;

/// <summary>
/// Walks the <see cref="Exception.InnerException" /> chain once, so the classifiers that inspect a
/// whole chain for retryable causes share the traversal instead of each re-implementing the loop.
/// Each caller supplies its own predicate; the type sets deliberately differ by call site.
/// </summary>
public static class ExceptionChain
{
    /// <summary>Yields <paramref name="exception" /> and each wrapped exception, outermost first.</summary>
    public static IEnumerable<Exception> Enumerate(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>True when any exception in the chain satisfies <paramref name="predicate" />.</summary>
    public static bool Contains(Exception? exception, Func<Exception, bool> predicate) =>
        Enumerate(exception).Any(predicate);
}
