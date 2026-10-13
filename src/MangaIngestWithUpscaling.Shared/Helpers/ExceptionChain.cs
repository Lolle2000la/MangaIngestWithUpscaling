namespace MangaIngestWithUpscaling.Shared.Helpers;

/// <summary>
/// Walks an exception chain once, so the classifiers that inspect a whole chain for retryable causes
/// share the traversal instead of each re-implementing the loop. The walk follows
/// <see cref="Exception.InnerException" /> and, because a sibling inside an
/// <see cref="AggregateException" /> is just as reachable as a wrapped cause, also each entry of
/// <see cref="AggregateException.InnerExceptions" />. Each caller supplies its own predicate; the
/// type sets deliberately differ by call site.
/// </summary>
public static class ExceptionChain
{
    /// <summary>
    /// Yields <paramref name="exception" /> and each reachable exception, outermost first along the
    /// inner-exception spine with an aggregate's siblings visited after that spine. A repeated
    /// instance or a self-referencing chain is yielded once rather than looping for ever.
    /// </summary>
    public static IEnumerable<Exception> Enumerate(Exception? exception)
    {
        if (exception is null)
        {
            yield break;
        }

        // Reference equality: an exception type may override Equals, and two distinct failures must
        // both be visited; only the *same* instance is a cycle.
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0)
        {
            Exception current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is AggregateException aggregate)
            {
                // Push in reverse so the first inner exception — the one InnerException exposes, and
                // the old single-chain walked — is popped next, keeping the spine-first order.
                for (int i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                {
                    pending.Push(aggregate.InnerExceptions[i]);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }

            yield return current;
        }
    }

    /// <summary>True when any exception reachable from the chain satisfies <paramref name="predicate" />.</summary>
    public static bool Contains(Exception? exception, Func<Exception, bool> predicate) =>
        Enumerate(exception).Any(predicate);
}
