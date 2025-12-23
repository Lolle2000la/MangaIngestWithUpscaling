FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# Install the required dependencies for the service
RUN apt-get update && apt-get install -y \
	python3 python3-venv wget && \
	rm -rf /var/lib/apt/lists/*
# Create a virtual environment and install packages
RUN python3 -m venv ./pyenv && \
	./pyenv/bin/python -m pip install --no-cache-dir  -U pip wheel --no-warn-script-location && \
	./pyenv/bin/python -m pip install --no-cache-dir torch==2.9.1 torchvision==0.24.1 --extra-index-url https://download.pytorch.org/whl/xpu \
		chainner_ext==0.3.10 numpy==2.2.5 opencv-python-headless==4.11.0.86 \
        psutil==6.0.0 pynvml==11.5.3 pyvips==3.0.0 pyvips-binary==8.16.1 rarfile==4.2 \
        sanic==24.6.0 spandrel_extra_arches==0.2.0 spandrel==0.4.1 packaging==25.0
ENV Ingest_Upscaler__PythonEnvironmentDirectory=/app/pyenv
ENV Ingest_Upscaler__SelectedDeviceIndex=0
ENV Ingest_Upscaler__ForceAcceptExistingEnvironment=true
