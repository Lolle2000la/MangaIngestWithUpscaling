# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM ghcr.io/lolle2000la/manga-ingest-with-upscaling-base:latest-xpu AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM --platform=$BUILDPLATFORM  mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj", "MangaIngestWithUpscaling/"]
RUN dotnet restore "./MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj"
COPY . .
WORKDIR "/src/MangaIngestWithUpscaling"
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
