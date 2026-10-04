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
    public Task RunAsync(
        CancellationTokenSource cts,
        Func<int?> taskIdProvider,
        Func<int, KeepAliveRequest> requestFactory
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
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        int? id = taskIdProvider();
                        if (!id.HasValue)
                        {
                            break;
                        }

                        KeepAliveResponse? ka = await client.KeepAliveAsync(
                            requestFactory(id.Value),
                            deadline: DateTime.UtcNow.AddSeconds(10),
                            cancellationToken: cts.Token
                        );
                        if (!ka.IsAlive)
                        {
                            await cts.CancelAsync();
                            break;
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                    {
                        await cts.CancelAsync();
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A keep-alive lapse past the server's deadline requeues the task; log it so a
                        // persistent failure is diagnosable instead of silent.
                        logger.LogDebug(ex, "Keep-alive for the in-flight task failed.");
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
