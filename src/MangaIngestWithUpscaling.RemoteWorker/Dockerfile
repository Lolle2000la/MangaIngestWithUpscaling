FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget ca-certificates libvulkan1 mesa-vulkan-drivers libgomp1 \
    libjpeg-dev zlib1g-dev libtiff-dev libwebp-dev libopenjp2-7-dev && \
    rm -rf /var/lib/apt/lists/*
ENV Ingest_Upscaler__SelectedDeviceIndex=1
ENV Ingest_Upscaler__PreferredGpuBackend=Auto

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["src/MangaIngestWithUpscaling.RemoteWorker/MangaIngestWithUpscaling.RemoteWorker.csproj", "src/MangaIngestWithUpscaling.RemoteWorker/"]
COPY ["src/MangaIngestWithUpscaling.Shared/MangaIngestWithUpscaling.Shared.csproj", "src/MangaIngestWithUpscaling.Shared/"]
RUN dotnet restore "./src/MangaIngestWithUpscaling.RemoteWorker/MangaIngestWithUpscaling.RemoteWorker.csproj" -p:OnnxRuntimeFlavor=Cpu
COPY . .
WORKDIR "/src/src/MangaIngestWithUpscaling.RemoteWorker"
RUN dotnet build "./MangaIngestWithUpscaling.RemoteWorker.csproj" -c Release -o /app/build -p:OnnxRuntimeFlavor=Cpu

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
RUN dotnet publish "./MangaIngestWithUpscaling.RemoteWorker.csproj" -c Release -a $TARGETARCH -o /app/publish /p:UseAppHost=false /p:PublishAot=false -p:OnnxRuntimeFlavor=Cpu

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV Ingest_Upscaler__ModelsDirectory=/models/MangaJaNai
VOLUME /models
VOLUME /data
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=3s --start-period=300s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

LABEL org.opencontainers.image.source="https://github.com/Lolle2000la/MangaIngestWithUpscaling"
ENTRYPOINT ["dotnet", "MangaIngestWithUpscaling.RemoteWorker.dll"]
