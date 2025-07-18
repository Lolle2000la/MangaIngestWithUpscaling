# Remote-Only Docker Variant

This document describes the remote-only Docker variant of MangaIngestWithUpscaling, designed for deployment scenarios where upscaling is handled exclusively by remote workers.

## Overview

The remote-only variant (`remoteonly.Dockerfile`) creates a lightweight container that runs the server component without any machine learning dependencies like PyTorch or the Python environment. All upscaling tasks are delegated to remote worker instances running on separate machines with GPU capabilities.

## Key Features

- **No ML Dependencies**: Does not include PyTorch, Python virtual environments, or any AI/ML libraries
- **Smaller Image Size**: Significantly reduced image size compared to the full variants
- **Lower Resource Requirements**: Perfect for running on resource-constrained servers
- **Pre-configured**: Automatically sets `Ingest_Upscaler__RemoteOnly=true`
- **Clean Separation**: Complete separation between the web server and compute-intensive tasks

## Docker Images

The remote-only variant is built and published automatically with the following tags:

**Release builds:**
- `ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-remote-only`
- `ghcr.io/lolle2000la/manga-ingest-with-upscaling:<version>-remote-only`

**Development builds:**
- `ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-dev-remote-only`
- `ghcr.io/lolle2000la/manga-ingest-with-upscaling:<timestamp>-remote-only`

## Configuration

The remote-only variant is pre-configured with:
- `Ingest_Upscaler__RemoteOnly=true` - Disables local upscaling
- Standard database and logging connections
- All gRPC endpoints for remote worker communication

## Docker Compose Example

```yaml
version: '3.9'

services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest-remote-only
    restart: unless-stopped
    environment:
      TZ: Europe/Berlin  # Set your timezone
      # Database connections
      Ingest_ConnectionStrings__DefaultConnection: "Data Source=/data/data.db;Pooling=false"
      Ingest_ConnectionStrings__LoggingConnection: "Data Source=/data/logs.db;Pooling=false"
      # Remote-only configuration (pre-set in the image)
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
      # map additional folders to access your files, e.g.:
      - ./ingest:/ingest  # Ingest folder
      - ./library:/library  # Output library (original and upscaled are subfolders)
    ports:
      - "8080:8080"  # Web interface
      - "8081:8081"  # gRPC for remote workers (required! otherwise remote workers will NOT work)
    networks:
      - manga-network

networks:
  manga-network:
    driver: bridge
```

## Requirements

To use the remote-only variant, you **must** set up at least one remote worker:

1. **Remote Worker Setup**: Follow the [Remote Worker Documentation](./REMOTE_WORKER.md)
2. **Network Connectivity**: Ensure remote workers can reach the server on port 8081 (gRPC)
3. **API Keys**: Configure API keys for remote worker authentication

## Use Cases

The remote-only variant is ideal for:

- **Separate Compute Resources**: Running the web interface on a lightweight server while having dedicated GPU machines for processing
- **NAS Deployments**: Running on NAS devices or low-power servers that do not support heavy ML workloads (e.g., Raspberry Pi, Synology NAS)
- **Scalable Architecture**: Multiple remote workers can connect to a single server instance, allowing you to take advantage of your existing hardware resources without sacraficing it to your server
- **Development/Testing**: Testing the server component without needing local GPU resources

## Image Size Comparison

| Variant | Approximate Size | ML Dependencies | Python Environment |
|---------|------------------|-----------------|-------------------|
| CUDA | ~7-8 GB | ✅ PyTorch CUDA | ✅ Full Python env |
| ROCm | ~15+ GB | ✅ PyTorch ROCm | ✅ Full Python env |
| **Remote-Only** | **~500 MB** | ❌ None | ❌ None |

## Building Locally

To build the remote-only variant locally:

```bash
# Clone the repository
git clone https://github.com/Lolle2000la/MangaIngestWithUpscaling.git
cd MangaIngestWithUpscaling

# Build the remote-only variant
docker build -f remoteonly.Dockerfile -t manga-ingest-remote-only .

# Run with docker-compose profile
docker-compose --profile remote-only up
```

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

**Q: Image is still large**
A: The remote-only image should be significantly smaller (~500MB vs ~4-5GB). If not:
- Ensure you're using the correct image tag (`-remote-only`)
- Check that you're not accidentally using the CUDA or ROCm variants

## Related Documentation

- [Remote Worker Setup](./REMOTE_WORKER.md)
- [Main README](../README.md)
- [Docker Deployment Guide](../README.md#installation)
