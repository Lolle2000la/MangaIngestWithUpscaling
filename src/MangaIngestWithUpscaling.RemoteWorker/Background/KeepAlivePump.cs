using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Sends a keep-alive for the in-flight task every interval until its
/// <see cref="CancellationTokenSource" /> is cancelled, so the server does not requeue work that is
/// still in progress. The request factory lets the caller vary the payload (for example the prefetch
/// hint) without duplicating the timer, error handling and cancellation semantics.
/// </summary>
public sealed class KeepAlivePump(IServiceScopeFactory serviceScopeFactory)
{
    /// <summary>Runs the pump until <paramref name="cts" /> is cancelled or the task is gone.</summary>
    /// <param name="cts">Owns the pump's lifetime; cancelled when the keep-alive should stop.</param>
    /// <param name="taskIdProvider">
    /// Returns the task to keep alive, or <see langword="null" /> to stop (for example once the
    /// streaming loop has cleared the in-flight task).
    /// </param>
    /// <param name="requestFactory">Builds the keep-alive request for a task id.</param>
    /// <param name="onTaskGone">
    /// Invoked when the server reports the task is gone, before the pump cancels
    /// <paramref name="cts" />. Lets the owner distinguish a server-forced stop from a real failure.
    /// </param>
    public Task RunAsync(
        CancellationTokenSource cts,
        Func<int?> taskIdProvider,
        Func<int, KeepAliveRequest> requestFactory,
        Action? onTaskGone = null
    )
    {
        return Task.Run(
            async () =>
            {
                using IServiceScope scope = serviceScopeFactory.CreateScope();
                var client =
                    scope.ServiceProvider.GetRequiredService<UpscalingService.UpscalingServiceClient>();
                var logger = scope.ServiceProvider.GetRequiredService<
                    ILogger<RemoteTaskProcessor>
                >();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                bool loggedFailure = false;
                while (!cts.IsCancellationRequested)
                {
                    int? id = null;
                    try
                    {
                        id = taskIdProvider();
                        if (!id.HasValue)
                        {
                            break;
                        }

                        KeepAliveResponse? ka = await client.KeepAliveAsync(
                            requestFactory(id.Value),
                            deadline: DateTime.UtcNow.AddSeconds(10),
                            cancellationToken: cts.Token
                        );
                        // A success response carrying IsAlive = false is a defensive fallback: the
                        // current server signals a task it no longer tracks by setting the RPC status
                        // to NotFound (handled below), which replaces this response, so this branch is
                        // not reached against it. Either shape means the task is gone.
                        if (!ka.IsAlive)
                        {
                            onTaskGone?.Invoke();
                            await cts.CancelAsync();
                            break;
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                    {
                        // The server no longer tracks the task (requeued or cancelled); stop and tell
                        // the owner so its stream can end cleanly instead of being counted as a
                        // failure.
                        onTaskGone?.Invoke();
                        await cts.CancelAsync();
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A keep-alive lapse past the server's deadline requeues the task, so this is
                        // operator-visible at Warning rather than Debug. Log it once per pump: the
                        // retry runs every 15 s, and a persistent transport fault would otherwise
                        // repeat the same line for the life of the task.
                        if (!loggedFailure)
                        {
                            loggedFailure = true;
                            logger.LogWarning(
                                ex,
                                "Keep-alive for in-flight task {TaskId} failed; if it keeps failing the server will requeue the task.",
                                id
                            );
                        }
                    }

                    try
                    {
                        await timer.WaitForNextTickAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            },
            cts.Token
        );
    }
}
