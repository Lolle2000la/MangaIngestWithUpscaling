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

For information about the remote-only server variant without ML dependencies, see the [Remote-Only Variant Documentation](./docs/REMOTE_ONLY_VARIANT.md).

## Usage

1. **Set up a library** through the UI.
2. **Ingest manga** by uploading putting the manga into the ingest folder you just configured.
3. Profit!

## Installation

The preferred way to run the application is through Docker. Below is an example docker-compose file to get you started with CUDA. See below for ROCm (AMD) support.

```yaml
version: '3.9'

services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: #your timezone here
      Ingest_Upscaler__SelectedDeviceIndex: 0 # if you have multiple GPUs, you can select which one to use
      Ingest_Upscaler__UseFp16: true # if you want to use fp16 instead of fp32, preferred if you have a GPU that supports it
      Ingest_Upscaler__UseCPU: false # if you want to use the CPU instead of the GPU
      # Kavita integration
      #Ingest_Kavita__BaseUrl: http://kavita:5000 # the base URL of your Kavita instance
      #Ingest_Kavita__ApiKey: #Your API key here
      #Ingest_Kavita__Enabled: True # defaults to false
      # OIDC Authentication (v0.12.0+)
      #Ingest_OIDC__Enabled: false # Set to true to enable OIDC authentication
      #Ingest_OIDC__Authority: # Your OIDC provider's authority URL (e.g., https://authentik.yourdomain.com/application/o/your-app/)
      #Ingest_OIDC__ClientId: # Your OIDC client ID
      #Ingest_OIDC__ClientSecret: # Your OIDC client secret
      #Ingest_OIDC__MetadataAddress: # Optional: Full URL to the OIDC discovery document (e.g., https://authentik.yourdomain.com/application/o/your-app/.well-known/openid-configuration)
                                   # Usually not needed if Authority is set correctly.
    volumes:
      - /path/to/store/appdata:/data # for storing the database and logs
      - /path/to/store/models:/models # for storing the upscaling models. 
      # ... other folders you want to be able to access from the container
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080 # the web interface will be available on this port
    #user: '1000:1000' # change the user/group for improved security. Note: The user must be part of the 'video' group and have the correct permissions for its mount points.
    # Make sure you have the nvidia-container-toolkit installed on your host.
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
```

If you run a ROCm-compatible AMD GPU, you can use the following docker-compose file:

```yaml
version: '3.9'

services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-rocm
    restart: unless-stopped
    environment:
      TZ: #your timezone here
      Ingest_Upscaler__SelectedDeviceIndex: 0 # if you have multiple GPUs, you can select which one to use
      Ingest_Upscaler__UseFp16: true # if you want to use fp16 instead of fp32, preferred if you have a GPU that supports it
      Ingest_Upscaler__UseCPU: false # if you want to use the CPU instead of the GPU
      # Kavita integration
      #Ingest_Kavita__BaseUrl: http://kavita:5000 # the base URL of your Kavita instance
      #Ingest_Kavita__ApiKey: #Your API key here
      #Ingest_Kavita__Enabled: True # defaults to false
      # OIDC Authentication (v0.12.0+)
      #Ingest_OIDC__Enabled: false # Set to true to enable OIDC authentication
      #Ingest_OIDC__Authority: # Your OIDC provider's authority URL (e.g., https://authentik.yourdomain.com/application/o/your-app/)
      #Ingest_OIDC__ClientId: # Your OIDC client ID
      #Ingest_OIDC__ClientSecret: # Your OIDC client secret
      #Ingest_OIDC__MetadataAddress: # Optional: Full URL to the OIDC discovery document (e.g., https://authentik.yourdomain.com/application/o/your-app/.well-known/openid-configuration)
                                   # Usually not needed if Authority is set correctly.
      # Rest is same as for the CUDA version
    volumes:
      - /path/to/store/appdata:/data # for storing the database and logs
      - /path/to/store/models:/models # for storing the upscaling models. 
      # ... other folders you want to be able to access from the container
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080 # the web interface will be available on this port
      - 8081:8081 # the gRPC interface will be available on this port (necessary for the remote worker)
    #user: '1000:1000' # change the user/group for improved security. Note: The user must be part of the 'video' group and have the correct permissions for its mount points.
    # The following lines are necessary to run the container with a ROCm-compatible AMD GPU.
    # See https://rocm.docs.amd.com/projects/install-on-linux/en/latest/how-to/docker.html for more information.
    devices:
      - /dev/kfd
      - /dev/dri
    security_opt:
      - seccomp:unconfined
```

I do not have an AMD GPU, so I cannot test this. If you have any issues, please open an issue.

### Remote-Only Variant

For users who want to run the server component without any machine learning dependencies and handle upscaling exclusively through remote workers, a special "remote-only" variant is available:

```yaml
version: '3.9'

services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-remote-only
    restart: unless-stopped
    environment:
      TZ: #your timezone here
      Ingest_Upscaler__RemoteOnly: true # This is set automatically in the remote-only image
      # Kavita integration
      #Ingest_Kavita__BaseUrl: http://kavita:5000 # the base URL of your Kavita instance
      #Ingest_Kavita__ApiKey: #Your API key here
      #Ingest_Kavita__Enabled: True # defaults to false
      # OIDC Authentication (v0.12.0+)
      #Ingest_OIDC__Enabled: false # Set to true to enable OIDC authentication
      #Ingest_OIDC__Authority: # Your OIDC provider's authority URL (e.g., https://authentik.yourdomain.com/application/o/your-app/)
      #Ingest_OIDC__ClientId: # Your OIDC client ID
      #Ingest_OIDC__ClientSecret: # Your OIDC client secret
      #Ingest_OIDC__MetadataAddress: # Optional: Full URL to the OIDC discovery document
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

**Benefits of the Remote-Only Variant:**
- **Smaller image size**: No PyTorch or ML dependencies included
- **Lower resource requirements**: Perfect for running on resource-constrained servers
- **Cleaner separation**: All upscaling is handled by dedicated remote worker machines
- **Automatic configuration**: `RemoteOnly` is pre-configured to `true`

This variant requires you to set up one or more [remote workers](./docs/REMOTE_WORKER.md) on separate machines with GPU capabilities to handle the actual upscaling tasks.

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

- .NET 9.0 SDK or later

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

The application relies on `appsettings.json` for configuration. Modify the connection strings and other parameters as needed.
Alternatively, you can use environment variables to override the configuration values.

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data.db;Pooling=false",
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
- **Database:** SQLite
- **Logging:** Serilog
- **Reactive Programming:** ReactiveUI (a tiny bit)

## Contributing

1. Fork the repository.
2. Create a new feature branch.
3. Commit changes and push to the branch.
4. Open a pull request.

