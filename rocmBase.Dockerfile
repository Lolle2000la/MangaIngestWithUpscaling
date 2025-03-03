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
	./pyenv/bin/python -m pip install --no-cache-dir torch torchvision --index-url https://download.pytorch.org/whl/rocm6.2.4 && \
	./pyenv/bin/python -m pip install --no-cache-dir opencv-python-headless==4.11.0.86 spandrel==0.4.0 spandrel_extra_arches==0.2.0 chainner_ext==0.3.10 numpy==2.1.3 psutil==6.0.0 pynvml==11.5.3 pyvips==2.2.3 pyvips-binary==8.16.0 rarfile==4.2 sanic==24.6.0
ENV Ingest_Upscaler__PythonEnvironmentDirectory=/app/pyenv
ENV Ingest_Upscaler__SelectedDeviceIndex=0