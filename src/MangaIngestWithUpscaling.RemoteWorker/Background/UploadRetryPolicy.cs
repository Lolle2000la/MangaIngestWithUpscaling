using System.Net.Sockets;
using Grpc.Core;
using MangaIngestWithUpscaling.RemoteWorker.Configuration;

namespace MangaIngestWithUpscaling.RemoteWorker.Background;

/// <summary>
/// Pure helpers for sizing upload attempt timeouts, spacing retries, and deciding whether a failed
/// upload attempt is worth retrying. Kept separate from <see cref="RemoteTaskProcessor"/> so they
/// can be tested without a gRPC channel.
/// </summary>
internal static class UploadRetryPolicy
{
    private static readonly TimeSpan MaxAttemptTimeout = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sizes the gRPC deadline for one attempt from the full file size, so a slow but healthy
    /// transfer is not cut off at an arbitrary fixed time. The full size is used rather than only
    /// the bytes still to send because the call's deadline also has to cover the server hashing,
    /// assembling and moving the whole file; on a near-complete resume those are far larger than
    /// the remaining transfer. The floor keeps small files and the assembly from being cut short.
    /// </summary>
    public static TimeSpan ComputeAttemptTimeout(long fileBytes, WorkerConfig config)
    {
        long throughput = Math.Max(1, config.UploadMinThroughputBytesPerSecond);
        TimeSpan scaled = TimeSpan.FromSeconds((double)fileBytes / throughput);
        TimeSpan timeout = scaled > config.UploadTimeoutFloor ? scaled : config.UploadTimeoutFloor;
        return timeout < MaxAttemptTimeout ? timeout : MaxAttemptTimeout;
    }

    /// <summary>
    /// Exponential backoff between attempts, capped so a long outage still retries periodically.
    /// </summary>
    public static TimeSpan GetRetryDelay(int attempt, WorkerConfig config)
    {
        double seconds = config.UploadRetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1);
        TimeSpan delay = TimeSpan.FromSeconds(seconds);
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    /// <summary>
    /// Whether another attempt is allowed, bounded both by the attempt cap and by the elapsed retry
    /// budget. The elapsed bound is what makes the worker survive an outage of a given duration
    /// rather than a given number of attempts. Callers measure <paramref name="elapsed"/> from the
    /// first failure, so healthy transfer time before it does not count against the budget.
    /// </summary>
    public static bool CanRetry(int attempt, TimeSpan elapsed, WorkerConfig config) =>
        attempt < config.UploadMaxAttempts && elapsed < config.UploadRetryMaxElapsed;

    /// <summary>
    /// Classifies an attempt failure. A missing local file or an explicit non-retryable rejection
    /// is terminal; transport, RPC and I/O failures are worth another resuming attempt.
    /// </summary>
    public static bool IsRetryable(Exception exception) =>
        exception switch
        {
            RetryableUploadException => true,
            NonRetryableUploadException => false,
            FileNotFoundException => false,
            DirectoryNotFoundException => false,
            IOException => true,
            SocketException => true,
            RpcException rpc
                when rpc.StatusCode
                    is StatusCode.Unavailable
                        or StatusCode.DeadlineExceeded
                        or StatusCode.ResourceExhausted
                        or StatusCode.Aborted
                        or StatusCode.Internal
                        or StatusCode.Cancelled
                        or StatusCode.Unknown => true,
            _ => false,
        };
}

/// <summary>A failure that may succeed if the upload is resumed and retried.</summary>
internal sealed class RetryableUploadException(string message) : Exception(message);

/// <summary>A failure the server reported as permanent; retrying would repeat it.</summary>
internal sealed class NonRetryableUploadException(string message) : Exception(message);
