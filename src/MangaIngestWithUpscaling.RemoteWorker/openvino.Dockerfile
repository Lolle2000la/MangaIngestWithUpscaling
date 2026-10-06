ARG BASE_IMAGE=openvino/ubuntu24_runtime:latest

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS dotnet-runtime

# Stage 1: Build the remote worker
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["src/MangaIngestWithUpscaling.RemoteWorker/MangaIngestWithUpscaling.RemoteWorker.csproj", "src/MangaIngestWithUpscaling.RemoteWorker/"]
COPY ["src/MangaIngestWithUpscaling.Shared/MangaIngestWithUpscaling.Shared.csproj", "src/MangaIngestWithUpscaling.Shared/"]
RUN dotnet restore "./src/MangaIngestWithUpscaling.RemoteWorker/MangaIngestWithUpscaling.RemoteWorker.csproj" -p:OnnxRuntimeFlavor=Managed
COPY . .
WORKDIR "/src/src/MangaIngestWithUpscaling.RemoteWorker"
RUN dotnet build "./MangaIngestWithUpscaling.RemoteWorker.csproj" -c Release -o /app/build -p:OnnxRuntimeFlavor=Managed

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
RUN dotnet publish "./MangaIngestWithUpscaling.RemoteWorker.csproj" -c Release -a $TARGETARCH -o /app/publish /p:UseAppHost=false /p:PublishAot=false -p:OnnxRuntimeFlavor=Managed

FROM ${BASE_IMAGE} AS final
USER root
WORKDIR /app

# Copy .NET 10 runtime
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
ENV LD_LIBRARY_PATH="/opt/intel/openvino/runtime/lib/intel64:${LD_LIBRARY_PATH}"

# Install dependencies for NetVips, .NET globalization, and official Intel ONNX Runtime with OpenVINO
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget ca-certificates python3-pip unzip libicu-dev \
    libjpeg-dev zlib1g-dev libtiff-dev libwebp-dev libopenjp2-7-dev && \
    pip install --break-system-packages --no-cache-dir onnxruntime-openvino && \
    cp -P /usr/local/lib/python3.12/dist-packages/onnxruntime/capi/libonnxruntime* /usr/lib/ 2>/dev/null || true && \
    rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

ENV Ingest_Upscaler__SelectedDeviceIndex=1
ENV Ingest_Upscaler__PreferredGpuBackend=OpenVINO
ENV Ingest_Upscaler__ModelsDirectory=/models/MangaJaNai
ENV ORT_OPENVINO_CACHE_DIR=/models/cache
VOLUME /models
VOLUME /data
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=3s --start-period=300s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

LABEL org.opencontainers.image.source="https://github.com/Lolle2000la/MangaIngestWithUpscaling"
ENTRYPOINT ["dotnet", "MangaIngestWithUpscaling.RemoteWorker.dll"]
