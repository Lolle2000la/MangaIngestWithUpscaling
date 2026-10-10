# Manga Ingest With Upscaling

## Overview

MangaIngestWithUpscaling is a **Blazor-based web application** designed to **ingest, process, and automatically upscale manga images**. 

## Features

- **Manga ingestion**: Ingest Mangas into a library structure useful for [Kavita](https://www.kavitareader.com/) and [Komga](https://komga.org/).
  - Keep track of mangas known under many different titles. This software will not just put them into the same folder, but also change the ComicInfo.xml file to reflect the new title.
  - Change the title of a manga. This may sound simple, but going back and changing the ComicInfo.xml file of a lot of chapters is quite a hassle.
- **Image upscaling**: Enhance image resolution with [MangaJaNai](https://github.com/the-database/mangajanai) upscaling models.
- **Image preprocessing**: 
  - **Automatic resizing**: Downscale images before upscaling to manage memory usage and improve performance.
  - **Format conversion**: Convert images between formats (PNG, JPEG, WebP, AVIF, etc.) before upscaling. By default, PNG images are automatically converted to high-quality JPG (quality 98) to ensure upscaler compatibility. See [Image Format Conversion Documentation](./docs/IMAGE_FORMAT_CONVERSION.md). 

## Remote Worker

For information on how to set up and use a remote worker for upscaling, please see the [Remote Worker Documentation](./docs/REMOTE_WORKER.md).

For information about running the server without local ML dependencies and delegating all
upscaling to remote workers, see the [Remote Worker Documentation](./docs/REMOTE_WORKER.md) and set
`Ingest_Upscaler__RemoteOnly=true` — see [the remote-only configuration](./docs/REMOTE_ONLY_VARIANT.md),
which is deprecated in favour of that environment variable.

## Usage

1. **Set up a library** through the UI.
2. **Ingest manga** by uploading putting the manga into the ingest folder you just configured.
3. Profit!

## Installation

The preferred way to run the application is through Docker. The standard image supports all GPU backends — you select the backend via an environment variable.

### ONNX Runtime and Model Storage

The application runs pure C# using ONNX Runtime for ML inference. There is no Python or PyTorch deployment needed:

- **Ultra-lightweight footprint** — no multi-gigabyte PyTorch/CUDA wheels downloaded
- **Native Hardware Acceleration** — one image per vendor: WebGPU on AMD Radeon, Intel and Apple, CUDA on NVIDIA, DirectML on Windows, plus a CPU fallback
- **Automatic Model Provisioning** — optimized ONNX models are downloaded on demand into the `/models` directory from GitHub releases
- **Persistent models** — models are cached in `/models` across updates

### Docker Image Flavors & Hardware Acceleration

Only two images are needed to cover all hardware:

| Image Tag | Hardware / Execution Provider | Target Hardware |
|---|---|---|
| `:latest` / `:latest-dev` | **Universal** (ONNX WebGPU EP via Vulkan + CPU fallback) | AMD Radeon (RX 5000/6000/7000/9000), Intel Arc / Iris Xe, CPU fallback |
| `:latest-cuda` / `:latest-dev-cuda` | **NVIDIA CUDA** & TensorRT Execution Providers | NVIDIA GeForce GTX/RTX, Quadro, Tesla (requires nvidia-container-toolkit) |

Both flavor tags are available for both the main application (`manga-ingest-with-upscaling`) and the remote worker (`manga-ingest-with-upscaling-remote-worker`).

---

### Universal Image (AMD Radeon / Intel / CPU)

Recommended for all non-NVIDIA systems. Uses ONNX Runtime WebGPU (backed by Mesa RADV for AMD and ANV for Intel via Vulkan). On systems without a GPU, it cleanly falls back to CPU inference.

Simply pass `/dev/dri` to the container to give it access to your GPU:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin
      Ingest_Upscaler__PreferredGpuBackend: Auto
      Ingest_Upscaler__SelectedDeviceIndex: 1 # 1 = first GPU, 0 = CPU
      Ingest_Upscaler__UseFp16: true
    volumes:
      - /path/to/store/appdata:/data
      - /path/to/store/models:/models
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080
      - 8081:8081
    devices:
      - /dev/dri # GPU access for Vulkan (Mesa RADV / ANV)
```

### NVIDIA GPU (CUDA)

For NVIDIA GPUs (e.g. RTX 3060, RTX 40-series), use the `:latest-cuda` image for maximum performance with TensorRT and CUDA:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-cuda
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin # your timezone
      Ingest_Upscaler__PreferredGpuBackend: CUDA
      Ingest_Upscaler__SelectedDeviceIndex: 1 # 1 = first GPU, 0 = CPU
      Ingest_Upscaler__UseFp16: true
    volumes:
      - /path/to/store/appdata:/data
      - /path/to/store/models:/models
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
```

See [GPU Backend Configuration](./docs/GPU_BACKEND_CONFIGURATION.md) for full details.

### Remote-Only Mode

If you already run a GPU machine for something else, you can keep the server itself free of any
machine-learning work by pointing it at [remote workers](./docs/REMOTE_WORKER.md) instead. Use the
standard image and switch local upscaling off:

```yaml
version: '3.9'

services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: #your timezone here
      # Enable remote-only mode - disables local upscaling
      Ingest_Upscaler__RemoteOnly: true
      # Kavita integration
      #Ingest_Kavita__BaseUrl: http://kavita:5000 # the base URL of your Kavita instance
      #Ingest_Kavita__ApiKey: #Your API key here
      #Ingest_Kavita__Enabled: True # defaults to false
      # OIDC Authentication (v0.12.0+)
      #Ingest_OIDC__Enabled: false # Set to true to enable OIDC
      #Ingest_OIDC__Authority: # Your OIDC provider's authority URL (e.g., https://authentik.yourdomain.com/application/manga/)
      #Ingest_OIDC__ClientId: # Your OIDC provider's client ID
      #Ingest_OIDC__ClientSecret: # Your OIDC provider's client secret
      #Ingest_OIDC__MetadataAddress: # Optional: Full URL to your OIDC discovery document
    volumes:
      - /path/to/store/appdata:/data # for storing the database and logs
      # ... other folders you want to be able to access from the container
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080 # the web interface will be available on this port
      - 8081:8081 # the gRPC interface will be available on this port (necessary for the remote worker)
    #user: '1000:1000' # change the user/group for improved security
```

With this set, the server loads no model at all and needs no GPU access; the `/models` volume can
be dropped. All upscaling is then handled by one or more
[remote workers](./docs/REMOTE_WORKER.md) on separate machines with GPU capabilities. See
[Remote-Only Configuration](./docs/REMOTE_ONLY_VARIANT.md) for the full option list.

### OIDC Configuration (v0.12.0+)

For version 0.12.0 and later, you can configure OpenID Connect (OIDC) for authentication. This allows you to use an external identity provider instead of the built-in user accounts.

To enable and configure OIDC when running with Docker, add the following environment variables to your `docker-compose.yml` under the `mangaingestwithupscaling.services.environment` section:

```yaml
# ... other environment variables ...
      Ingest_OIDC__Enabled: "true"  # Set to "true" to enable OIDC, "false" to disable
      Ingest_OIDC__Authority: "https://your-oidc-provider.com/auth/realms/your-realm" # URL of your OIDC provider (e.g., Keycloak, Authentik)
      Ingest_OIDC__ClientId: "your-client-id" # The Client ID registered with your OIDC provider
      Ingest_OIDC__ClientSecret: "your-client-secret" # The Client Secret for your OIDC client
      # Optional: Full URL to the OIDC discovery document. 
      # If your Authority URL is already the discovery endpoint (e.g., ends with /.well-known/openid-configuration), 
      # this might not be needed.
      # Ingest_OIDC__MetadataAddress: "https://your-oidc-provider.com/auth/realms/your-realm/.well-known/openid-configuration" 
# ... rest of your docker-compose.yml ...
```

Note that in most cases, you either need to set `Ingest_OIDC__Authority` or `Ingest_OIDC__MetadataAddress`, but not both. The `Authority` is often sufficient as long as the discovery document is accessible at the standard path (`/.well-known/openid-configuration`).

**Redirect URIs for your OIDC Provider:**

When configuring the OIDC client in your identity provider, you will need to specify the following redirect URIs:

*   **Login Redirect URI:** `https://<your-app-base-url>/signin-oidc`
    *   Replace `<your-app-base-url>` with the actual base URL where MangaIngestWithUpscaling is accessible (e.g., `https://manga.example.com`).
*   **Post-Logout Redirect URI:** `https://<your-app-base-url>/`
    *   This is where users will be redirected after logging out from the OIDC provider. You can adjust this to a different page if needed, but the application root is a common choice.

Make sure your OIDC provider is configured to accept these URIs.

**Problems when running behind a reverse proxy:**

If you encounter issues with OIDC authentication when running behind a reverse proxy, ensure that the proxy is correctly forwarding the necessary headers. You may need to configure your reverse proxy to pass through headers like `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` to maintain the correct request context.

In the case of nginx in particular, you might have to add the following configuration to your nginx server block (shoutout to [@dankennedy](https://github.com/DuendeArchive/IdentityServer4/issues/1670#issuecomment-340774293)):

```nginx
proxy_buffer_size          128k;

proxy_buffers              4 256k;

proxy_busy_buffers_size    256k;

# The following lines are necessary for the remote worker to work correctly with gRPC.
# Page streaming is process-local: if you run more than one server instance behind this proxy, a
# chapter's page RPCs (manifest, fetch, upload) must all reach the same instance. Pin a worker to one
# upstream (e.g. ip_hash, or a dedicated upstream per worker) rather than round-robining requests.
location / {
    # Detect gRPC traffic
    if ($http_content_type = "application/grpc") {
        # Use grpcs:// if your backend gRPC server has TLS enabled
        # Use grpc:// if your backend gRPC server does not have TLS enabled
        grpc_pass grpc://<your host>:<your grpc port, e.g., 8081>;
    }

    # Fallback for regular HTTP traffic
    proxy_pass http://<your host>:<your regular port, e.g., 8080>;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

Without this, after being redirected back to the application, you might be faced with a 502 Bad Gateway error or a blank page.

## Building Prerequisites

- .NET 10.0 SDK or later

## Running from Source

1. **Clone the repository:**
   ```sh
   git clone https://github.com/your-repo/MangaIngestWithUpscaling.git
   cd MangaIngestWithUpscaling
   ```
2. **Restore dependencies:**
   ```sh
   dotnet restore
   ```
3. **Build the project:**
   ```sh
   dotnet build
   ```
4. **Run the application:**
   ```sh
   dotnet run
   ```

## Configuration

For a full reference of all upscaler settings and their corresponding environment variables (the recommended approach for Docker deployments), see [Upscaler Configuration](./docs/UPSCALER_CONFIGURATION.md).

The application relies on `appsettings.json` for configuration. Modify the connection strings and other parameters as needed.
Alternatively, you can use environment variables to override the configuration values.

The database backend is selectable: SQLite is the default, and PostgreSQL is supported as an
alternative. See [Database Providers](./docs/DATABASE_PROVIDERS.md) for configuration and
provider-specific migration guidance, and the
[Database Migration Guide](./docs/DATABASE_MIGRATION.md) for moving an existing installation between
SQLite and PostgreSQL.

```json
{
  "DatabaseProvider": "Sqlite", // or "Postgres"
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data.db;Pooling=false",
    "PostgresConnection": "Host=localhost;Database=manga_ingest;Username=postgres;Password=postgres",
    "LoggingConnection": "Data Source=logs.db;Pooling=false"
  },
  "Serilog": {
    "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.SQLite" ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "Enrich": [ "FromLogContext" ]
  },
  "AllowedHosts": "*",
  "Upscaler": {
    "UseFp16": true,
    "UseCPU": false,
    "SelectedDeviceIndex": 0
  },
  "OIDC": {
    "Enabled": false, // Set to true to enable OIDC
    "Authority": "YOUR_OIDC_AUTHORITY_URL", // e.g., https://authentik.example.com/application/o/slug/
    "MetadataAddress": "YOUR_FULL_DISCOVERY_DOCUMENT_URL", // Optional, Authority is often sufficient if it's the discovery endpoint
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET"
  }
}

```

## Tech Stack

- **Frontend:** Blazor (MudBlazor components)
- **Backend:** ASP.NET Core
- **Database:** SQLite (default) or PostgreSQL
- **Logging:** Serilog
- **Reactive Programming:** ReactiveUI (a tiny bit)

## Contributing

1. Fork the repository.
2. Create a new feature branch.
3. Commit changes and push to the branch.
4. Open a pull request.

