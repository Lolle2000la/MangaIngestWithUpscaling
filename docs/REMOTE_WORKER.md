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