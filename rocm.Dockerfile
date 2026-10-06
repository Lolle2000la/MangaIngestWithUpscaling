ARG BASE_IMAGE=rocm/dev-ubuntu-24.04:latest

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS dotnet-runtime

# Stage 1: Build service
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["src/MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj", "src/MangaIngestWithUpscaling/"]
COPY ["src/MangaIngestWithUpscaling.Shared/MangaIngestWithUpscaling.Shared.csproj", "src/MangaIngestWithUpscaling.Shared/"]
COPY ["src/MangaIngestWithUpscaling.Data/MangaIngestWithUpscaling.Data.csproj", "src/MangaIngestWithUpscaling.Data/"]
COPY ["src/MangaIngestWithUpscaling.Data.Sqlite/MangaIngestWithUpscaling.Data.Sqlite.csproj", "src/MangaIngestWithUpscaling.Data.Sqlite/"]
COPY ["src/MangaIngestWithUpscaling.Data.Postgres/MangaIngestWithUpscaling.Data.Postgres.csproj", "src/MangaIngestWithUpscaling.Data.Postgres/"]
COPY ["tools/MangaIngestWithUpscaling.DbMigrator/MangaIngestWithUpscaling.DbMigrator.csproj", "tools/MangaIngestWithUpscaling.DbMigrator/"]
RUN dotnet restore "./src/MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj" -p:OnnxRuntimeFlavor=Managed
RUN dotnet restore "./tools/MangaIngestWithUpscaling.DbMigrator/MangaIngestWithUpscaling.DbMigrator.csproj"
COPY . .
WORKDIR "/src/src/MangaIngestWithUpscaling"
RUN dotnet build "./MangaIngestWithUpscaling.csproj" -c Release -o /app/build -p:OnnxRuntimeFlavor=Managed

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MangaIngestWithUpscaling.csproj" -c Release -a $TARGETARCH -o /app/publish /p:UseAppHost=false -p:OnnxRuntimeFlavor=Managed
RUN dotnet publish "/src/tools/MangaIngestWithUpscaling.DbMigrator/MangaIngestWithUpscaling.DbMigrator.csproj" -c Release -a $TARGETARCH -o /app/migrator /p:UseAppHost=false
RUN cp -rn /app/migrator/. /app/publish/

FROM ${BASE_IMAGE} AS final
WORKDIR /app

# Copy .NET 10 runtime
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
ENV LD_LIBRARY_PATH="/opt/rocm/lib:${LD_LIBRARY_PATH}"

# Install dependencies for NetVips and .NET globalization
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget ca-certificates libicu-dev \
    libjpeg-dev zlib1g-dev libtiff-dev libwebp-dev libopenjp2-7-dev && \
    rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

ENV Ingest_Upscaler__SelectedDeviceIndex=1
ENV Ingest_Upscaler__PreferredGpuBackend=ROCm
ENV Ingest_Upscaler__ModelsDirectory=/models/MangaJaNai
VOLUME /models
ENV Ingest_ConnectionStrings__DefaultConnection="Data Source=/data/data.db;Pooling=false"
ENV Ingest_ConnectionStrings__LoggingConnection="Data Source=/data/logs.db;Pooling=false"
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
EXPOSE 8081
VOLUME [ "/data" ]

HEALTHCHECK --interval=30s --timeout=3s --start-period=300s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

LABEL org.opencontainers.image.source="https://github.com/Lolle2000la/MangaIngestWithUpscaling"
ENTRYPOINT ["dotnet", "MangaIngestWithUpscaling.dll"]
