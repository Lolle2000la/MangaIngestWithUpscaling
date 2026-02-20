# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# Install the required dependencies for the service
RUN apt-get update && apt-get install -y \
	python3 python3-venv wget && \
	rm -rf /var/lib/apt/lists/*
# The Python virtual environment is installed at runtime into the data volume on first startup.
ENV Ingest_Upscaler__PythonEnvironmentDirectory=/data/pyenv
ENV Ingest_Upscaler__SelectedDeviceIndex=0
ENV Ingest_Upscaler__PreferredGpuBackend=ROCm
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM --platform=$BUILDPLATFORM  mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["src/MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj", "src/MangaIngestWithUpscaling/"]
COPY ["src/MangaIngestWithUpscaling.Shared/MangaIngestWithUpscaling.Shared.csproj", "src/MangaIngestWithUpscaling.Shared/"]
RUN dotnet restore "./src/MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj"
COPY . .
WORKDIR "/src/src/MangaIngestWithUpscaling"
RUN dotnet build "./MangaIngestWithUpscaling.csproj" -c Release -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MangaIngestWithUpscaling.csproj" -c Release -a $TARGETARCH -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# configure models save directory
ENV Ingest_Upscaler__ModelsDirectory=/models/MangaJaNai
VOLUME /models
ENV Ingest_ConnectionStrings__DefaultConnection="Data Source=/data/data.db;Pooling=false"
ENV Ingest_ConnectionStrings__LoggingConnection="Data Source=/data/logs.db;Pooling=false"
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
EXPOSE 8081
VOLUME [ "/data" ]
LABEL org.opencontainers.image.source="https://github.com/Lolle2000la/MangaIngestWithUpscaling"
ENTRYPOINT ["dotnet", "MangaIngestWithUpscaling.dll"]