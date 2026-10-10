ARG BASE_IMAGE=nvidia/cuda:13.4.2-cudnn-runtime-ubuntu24.04

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS dotnet-runtime

# Stage 1: Build the remote worker
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["src/MangaIngestWithUpscaling.RemoteWorker/MangaIngestWithUpscaling.RemoteWorker.csproj", "src/MangaIngestWithUpscaling.RemoteWorker/"]
COPY ["src/MangaIngestWithUpscaling.Shared/MangaIngestWithUpscaling.Shared.csproj", "src/MangaIngestWithUpscaling.Shared/"]
RUN dotnet restore "./src/MangaIngestWithUpscaling.RemoteWorker/MangaIngestWithUpscaling.RemoteWorker.csproj" -p:OnnxRuntimeFlavor=Cuda
COPY . .
WORKDIR "/src/src/MangaIngestWithUpscaling.RemoteWorker"
RUN dotnet build "./MangaIngestWithUpscaling.RemoteWorker.csproj" -c Release -o /app/build -p:OnnxRuntimeFlavor=Cuda

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
RUN dotnet publish "./MangaIngestWithUpscaling.RemoteWorker.csproj" -c Release -a $TARGETARCH -o /app/publish /p:UseAppHost=false /p:PublishAot=false -p:OnnxRuntimeFlavor=Cuda

FROM ${BASE_IMAGE} AS final
WORKDIR /app

# Copy .NET 10 runtime
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"

# Install dependencies for NetVips and .NET globalization
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget ca-certificates libicu-dev \
    libjpeg-dev zlib1g-dev libtiff-dev libwebp-dev libopenjp2-7-dev && \
    rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

ENV Ingest_Upscaler__SelectedDeviceIndex=1
ENV Ingest_Upscaler__PreferredGpuBackend=CUDA
ENV Ingest_Upscaler__ModelsDirectory=/models/MangaJaNai
VOLUME /models
VOLUME /data
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=3s --start-period=300s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

LABEL org.opencontainers.image.source="https://github.com/Lolle2000la/MangaIngestWithUpscaling"
ENTRYPOINT ["dotnet", "MangaIngestWithUpscaling.RemoteWorker.dll"]
