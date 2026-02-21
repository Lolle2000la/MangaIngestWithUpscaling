# Remote-Only Docker Variant (Deprecated)

> **⚠️ DEPRECATION NOTICE**
> The `-remote-only` image variant is deprecated and will be removed in a future release.
> Use the **standard image** and set `Ingest_Upscaler__RemoteOnly=true` to achieve the same result.
> See [Using the Standard Image](#using-the-standard-image) below for the recommended approach.

This document describes the remote-only configuration of MangaIngestWithUpscaling, designed for deployment scenarios where upscaling is handled exclusively by remote workers.

## Using the Standard Image

The recommended way to run in remote-only mode is to use the standard image with the `Ingest_Upscaler__RemoteOnly=true` environment variable:

```yaml
services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin  # Set your timezone
      # Enable remote-only mode - disables local upscaling
      Ingest_Upscaler__RemoteOnly: true
      # Optional: Kavita integration
      #Ingest_Kavita__BaseUrl: http://kavita:5000
      #Ingest_Kavita__ApiKey: your-api-key-here
      #Ingest_Kavita__Enabled: true
      # Optional: OIDC Authentication
      #Ingest_OIDC__Enabled: true
      #Ingest_OIDC__Authority: https://your-oidc-provider.com
      #Ingest_OIDC__ClientId: your-client-id
      #Ingest_OIDC__ClientSecret: your-client-secret
    volumes:
      - ./data:/data  # Database and logs
      - ./ingest:/ingest  # Ingest folder
      - ./library:/library  # Output library
    ports:
      - "8080:8080"  # Web interface
      - "8081:8081"  # gRPC for remote workers (required!)
```

> **Note:** The standard image includes `python3` and basic OS libraries, but the heavy ML dependencies (PyTorch, etc.) are downloaded and installed at runtime into the data volume on first startup. When running in remote-only mode, this runtime venv setup is skipped entirely, so the standard image is a good fit for remote-only deployments without any size concerns.

## Overview

The remote-only configuration runs the server component without using any local machine learning capabilities. All upscaling tasks are delegated to remote worker instances running on separate machines with GPU capabilities.

## Key Features

- **No local ML processing**: Local upscaling is disabled; all tasks go to remote workers
- **Lower Resource Usage**: Perfect for running on resource-constrained servers
- **Clean Separation**: Complete separation between the web server and compute-intensive tasks

## Configuration

When `Ingest_Upscaler__RemoteOnly=true` is set:
- Local upscaling is disabled
- All upscaling tasks are forwarded to remote workers via gRPC
- Standard database and logging connections still apply
- All gRPC endpoints for remote worker communication remain active

## Requirements

To use remote-only mode, you **must** set up at least one remote worker:

1. **Remote Worker Setup**: Follow the [Remote Worker Documentation](./REMOTE_WORKER.md)
2. **Network Connectivity**: Ensure remote workers can reach the server on port 8081 (gRPC)
3. **API Keys**: Configure API keys for remote worker authentication

## Use Cases

Remote-only mode is ideal for:

- **Separate Compute Resources**: Running the web interface on a lightweight server while having dedicated GPU machines for processing
- **NAS Deployments**: Running on NAS devices or low-power servers that do not support heavy ML workloads (e.g., Raspberry Pi, Synology NAS)
- **Scalable Architecture**: Multiple remote workers can connect to a single server instance, allowing you to take advantage of your existing hardware resources without sacrificing it to your server
- **Development/Testing**: Testing the server component without needing local GPU resources

## Troubleshooting

**Q: Upscaling tasks are not being processed**
A: Ensure that:
- At least one remote worker is running and connected
- Remote workers can reach the server on port 8081
- API keys are correctly configured
- Check the logs for connection errors

**Q: Remote workers cannot connect**
A: Verify that:
- Port 8081 is exposed and accessible
- HTTPS is properly configured (gRPC requires HTTP/2)
- Firewall allows connections on port 8081
- The `ApiUrl` in remote worker configuration points to the correct server

## Related Documentation

- [Remote Worker Setup](./REMOTE_WORKER.md)
- [Main README](../README.md)
- [Docker Deployment Guide](../README.md#installation)
