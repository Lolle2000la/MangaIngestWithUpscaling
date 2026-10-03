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

## Page streaming

Upscaling a chapter is streamed **page by page**. The worker fetches only the source pages the server is still missing, feeds them to the local upscaler as they arrive, and uploads each upscaled page as soon as it is written. The server spools the pages and assembles the final CBZ, so a dropped connection resumes at the first missing page instead of re-downloading and re-upscaling the whole chapter. Page streaming is the only transfer mode: whole-CBZ transfers are no longer supported by the worker or the server API, so **the worker and server must be upgraded together**. To make that explicit, the worker and server exchange an incrementing **protocol version** on connect (see `UpscalingProtocolVersion`): the server rejects a worker whose version is outside its supported range, the worker rejects a server whose version is outside its own, and a worker too old to send a version is rejected on connect rather than left to fail with `Unimplemented` on every task. A skewed `Unimplemented` response is treated as transient rather than a task failure.

Partial page state lives in the server's temp directory and is bounded by a 24-hour retention sweep. It is process-local: a different replica (or a restarted server) has no spool, so the chapter restarts from the beginning there rather than mixing bytes. The worker manifests once per attempt.

A transient transport failure (the server being briefly unreachable, or a request deadline) does **not** fail the task: the worker stops sending keep-alives and the server requeues the chapter with its spool intact, so the worker resumes at the first missing page — provided the re-dispatch reaches the same replica (a requeue is a fresh delegation; see the replica-affinity note below). The same non-terminal **restart** applies when the chapter's content, profile or engine changed mid-stream, or when a request hits a replica without the spool. Only a deterministic failure — for example a page the server does not have, or a storage error — is reported as a task failure, which clears the spool. A page the engine cannot decode is copied through unchanged (matching the local upscaler) rather than failing the chapter.

Every worker computes an opaque **engine identity** from its upscaler models, preprocessing configuration, build and resolved workflow (or its detector, for split detection) and sends it with the manifest and with each uploaded page. The server records the identity the chapter is spooled with: a manifest from a different engine **resets** the spool to the current identity so the chapter restarts cleanly on the new engine, and a page produced by a different engine is rejected. A chapter therefore cannot be assembled from pages produced by different models, preprocessing or builds. (The upscaler models are fingerprinted by relative path, size and a content prefix, so a same-size weight swap is detected unless it also shares the first 64 KiB; the cross-device exclusion of the selected GPU is best-effort.)

Because the spool is per-replica, **all of a chapter's page RPCs must reach the same server instance**: the manifest, the page fetches and the page uploads have to share one replica. Do not put page streaming behind a load balancer that spreads individual RPCs across replicas — that makes a chapter never complete. Keep each worker pinned to one replica (sticky sessions, a direct connection, or a single-replica deployment).

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