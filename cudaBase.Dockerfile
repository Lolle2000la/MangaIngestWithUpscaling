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