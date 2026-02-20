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