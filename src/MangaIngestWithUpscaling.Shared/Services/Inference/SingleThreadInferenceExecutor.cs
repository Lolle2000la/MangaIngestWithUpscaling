using System.Collections.Concurrent;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// Runs inference on one long-lived thread for the life of the process.
/// <para>
/// ONNX Runtime's CUDA execution provider keeps a per-thread context — handles plus a workspace — and
/// allocates it the first time a thread runs a session. Measured on an RTX 5070 Ti with the 1600p
/// ESRGAN model at 2x2 of 563x800: a process that had run inference on one thread held 74 MB more
/// than one that had not, a second thread added another 74 MB, and a run confined to one thread added
/// nothing per page. Measuring the same tiler directly: 7710 MB constant over ten pages on one
/// thread, +74 MB per page on the thread pool, because <c>Task.Run</c> hands each page to whichever
/// thread is free and every new thread pays that context again. That growth is what made the process's
/// device footprint creep from 7712 MB to 8738 MB over 24 pages while the tile plan stayed the same.
/// See <c>docs/UPSCALING_MEMORY_MODEL.md</c>.
/// </para>
/// <para>
/// One thread for the whole process bounds the cost at one context. It costs no throughput: the
/// upscaling queue runs one page at a time per process (the page's own work is already sequential),
/// and the tiler's look-ahead for the next tile's tensor is CPU work on the pool, not inference.
/// </para>
/// <para>
/// The work runs under the caller's <see cref="ExecutionContext"/>, so <see cref="AsyncLocal{T}"/>
/// values — logging scopes, the tiler's test seam, activity context — are the caller's, exactly as
/// they would be inside a <c>Task.Run</c>.
/// </para>
/// </summary>
public static class SingleThreadInferenceExecutor
{
    private static readonly Lazy<Worker> SharedWorker = new(
        static () => new Worker(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    /// <summary>
    /// Runs <paramref name="work"/> on the inference thread and returns its result. A call made from
    /// that thread runs inline, so a worker cannot queue behind itself.
    /// </summary>
    public static T Run<T>(Func<T> work) => SharedWorker.Value.Run(work);

    private sealed class Worker
    {
        private readonly BlockingCollection<WorkItem> queue = new();
        private readonly Thread thread;

        public Worker()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "onnx-inference" };
            thread.Start();
        }

        public T Run<T>(Func<T> work)
        {
            if (Environment.CurrentManagedThreadId == thread.ManagedThreadId)
            {
                return work();
            }

            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            queue.Add(new WorkItem(ExecutionContext.Capture(), () => work(), completion));
            return (T)completion.Task.GetAwaiter().GetResult()!;
        }

        private void Loop()
        {
            foreach (WorkItem item in queue.GetConsumingEnumerable())
            {
                try
                {
                    object? result = null;
                    if (item.Context is { } context)
                    {
                        ExecutionContext.Run(context, _ => result = item.Work(), null);
                    }
                    else
                    {
                        result = item.Work();
                    }

                    item.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // The caller's thread is where the failure belongs: it rethrows with the original
                    // stack, which is what the engine's memory-pressure descent and the model
                    // classification read.
                    item.Completion.TrySetException(ex);
                }
            }
        }

        private sealed class WorkItem(
            ExecutionContext? context,
            Func<object?> work,
            TaskCompletionSource<object?> completion
        )
        {
            public ExecutionContext? Context { get; } = context;

            public Func<object?> Work { get; } = work;

            public TaskCompletionSource<object?> Completion { get; } = completion;
        }
    }
}
