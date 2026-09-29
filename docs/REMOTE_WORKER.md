# Remote Worker Configuration

The remote worker is a separate application that can be run on a different machine to offload the resource-intensive task of image upscaling from the main application. This allows you to run the main application on a lower-power machine and have a dedicated, more powerful machine for processing.

## How it Works

The remote worker communicates with the main application over gRPC. The main application sends upscaling tasks to the remote worker, which then executes them and sends the results back.

## Configuration

The remote worker is configured via the `appsettings.json` file located in the `MangaIngestWithUpscaling.RemoteWorker` directory.

The important configuration sections are:

```json
{
  "WorkerConfig": {
    "ApiKey": "YOUR_API_KEY",
    "ApiUrl": "https://your-main-app-url:port"
  }
}
```

- `WorkerConfig:ApiKey`: This is the secret key used to authenticate with the main application's API. You can find the API key in the main application's UI under **`https://your-main-app-url:port/Account/Manage/ApiKeys`**.
- `WorkerConfig:ApiUrl`: This is the HTTPS URL of the main application's API. If you are running the remote worker on a different machine, you will need to change this to the IP address or hostname of the machine running the main application.

In addition, you can use environment variables to override the settings in `appsettings.json`. The environment variable names are prefixed with `Ingest_`, for example, `Ingest_WorkerConfig__ApiKey` and `Ingest_WorkerConfig__ApiUrl`. Configuring the upscaling is done in exactly the same way as in the server. 

## Communication

The remote worker communicates with the main application exclusively over HTTPS. gRPC, the underlying communication protocol, requires HTTP/2. Modern reverse proxies, when configured for HTTPS, will typically use HTTP/2 automatically for clients that support it. Ensure your reverse proxy hosting the main application has HTTPS and HTTP/2 enabled.

## Resilient uploads

Uploading an upscaled cbz is the longest single transfer the worker performs, and on a slow or
unreliable link it can take a while. Uploads are therefore **resumable**: the server stores each
uploaded chunk in a per-task directory and reports how many contiguous chunks it already has, so
after a dropped connection the worker retries only the missing bytes instead of re-sending the whole
file. The worker also sends HTTP/2 keepalive pings, which turn a silently dead connection into a
fast, retryable failure rather than a long hang.

Resume state is bound to a **content identity** (a SHA-256 of the file being uploaded). If the same
task is re-dispatched with a different upscaled output — for example after an upscaler profile or
model change — the stored chunks no longer match and are discarded, so old and new bytes are never
mixed. The server also hashes the assembled file and rejects it if it does not match the identity,
which catches truncation or corruption; the worker then re-uploads from scratch.

The per-attempt gRPC deadline is sized from the whole file, not just the bytes still to send,
because the server has to hash, assemble and move the entire file even when a resume only re-sends
the last chunk. A legitimately slow-but-healthy transfer is therefore not cut off by a fixed
timeout. These settings can be tuned in the
worker's `WorkerConfig` section (or via `Ingest_WorkerConfig__…` environment variables):

| Setting | Default | Purpose |
| --- | --- | --- |
| `UploadMaxAttempts` | `10` | Hard cap on attempts before the task is reported as failed. |
| `UploadRetryBaseDelay` | `00:00:10` | Delay before the first retry; subsequent retries back off exponentially (capped at 5 minutes). |
| `UploadRetryMaxElapsed` | `00:10:00` | How long to keep retrying a failing upload before giving up, measured from the first failure (a long but healthy initial attempt does not count against it). Bound the outages you want to survive here. |
| `UploadTimeoutFloor` | `00:02:00` | Minimum per-attempt deadline. |
| `UploadMinThroughputBytesPerSecond` | `131072` | Assumed worst-case speed used to size the per-attempt deadline. |

If an outage lasts longer than `UploadRetryMaxElapsed`, the attempts are abandoned and the task has
to be re-downloaded and re-upscaled, so size that budget for the outages you expect.

When the worker is hosted behind a reverse proxy, make sure the proxy does not cut long-lived gRPC
streams or reject large bodies: for nginx, raise `grpc_read_timeout` and `grpc_send_timeout` (both
default to 60 seconds) well above the expected upload time, and raise `client_max_body_size` (it
defaults to `1m` and will otherwise reject an upscaled cbz).

The server keeps partial uploads for 24 hours by default before a periodic cleanup removes them
(`Uploads:ChunkRetention` / `Uploads:CleanupInterval`, or the `Ingest_Uploads__…` environment
variables). A link that cannot sustain Kestrel's minimum request body data rate (240 B/s) will have
each attempt aborted, but resumption still lets it make progress across attempts as long as the
retry budget above allows.

Partial uploads are stored as local temporary files, so resume works only against the same server
process. If the server restarts, its temp directory is cleared, or the worker reconnects to a
different replica behind a load balancer, the server reports no progress and the worker safely
starts that upload over from the beginning rather than corrupting it.

Because the upload request-body size limit is lifted, the server bounds each task's upload itself:
chunks are accepted only for a task that exists and is not cancelled or failed, at most
`Uploads:MaxTotalChunks` chunks per task (default 16384), each at most `Uploads:MaxChunkBytes` bytes
(default 16 MiB). The effective per-task disk bound is `Uploads:MaxTaskBytes` (default 16 GiB),
which is what prevents a client using larger chunks from occupying up to
`MaxTotalChunks × MaxChunkBytes` (about 256 GiB).

## Running the Remote Worker

To run the remote worker:

1.  Ensure you have the .NET runtime installed on the machine that will run the worker.
2.  Build the `MangaIngestWithUpscaling.RemoteWorker` project.
3.  Copy the build output to the machine where you want to run the worker.
4.  Modify the `appsettings.json` file with the correct `ApiKey` and `ApiUrl`.
5.  Run the `MangaIngestWithUpscaling.RemoteWorker` executable.

The remote worker will then connect to the main application and be ready to receive upscaling tasks.

There are prebuilt binaries available in AppImage format for Linux and a Windows executable. You can find these in the GitHub Release assets.

Note that you need to configure the remote worker either with a `appsettings.json` file in the same directory as the executable or by using environment variables as described above.