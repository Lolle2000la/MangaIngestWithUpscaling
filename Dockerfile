# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS base
WORKDIR /app
# Install the required dependencies for the service
RUN apt-get update && apt-get install -y \
	python3 python3.12-venv wget && \
	rm -rf /var/lib/apt/lists/*
COPY ["MangaJaNaiConverterGui/MangaJaNaiConverterGui/backend/", "./backend"]
# Create a virtual environment and install packages
RUN python3 -m venv ./pyenv && \
	./pyenv/bin/python -m pip install --no-cache-dir  -U pip wheel --no-warn-script-location && \
	./pyenv/bin/python -m pip install --no-cache-dir  "./backend/src" --no-warn-script-location && \
	./pyenv/bin/python -m pip uninstall --yes opencv-python && \
	./pyenv/bin/python -m pip install --no-cache-dir opencv-python-headless==4.11.0.86
ENV Ingest_Upscaler__PythonEnvironmentDirectory=/app/pyenv
ENV Ingest_Upscaler__SelectedDeviceIndex=0
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj", "MangaIngestWithUpscaling/"]
RUN dotnet restore "./MangaIngestWithUpscaling/MangaIngestWithUpscaling.csproj"
COPY . .
WORKDIR "/src/MangaIngestWithUpscaling"
RUN dotnet build "./MangaIngestWithUpscaling.csproj" -c Release -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MangaIngestWithUpscaling.csproj" -c Release -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# configure models save directory
ENV Ingest_Upscaler__ModelsDirectory=/models/MangaJaNai
VOLUME /models
ENV Ingest_ConnectionStrings__DefaultConnection="Data Source=/data/data.db;Pooling=false"
ENV Ingest_ConnectionStrings__LoggingConnection="Data Source=/data/logs.db;Pooling=false"
VOLUME [ "/data" ]
ENTRYPOINT ["dotnet", "MangaIngestWithUpscaling.dll"]