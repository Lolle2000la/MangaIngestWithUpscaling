# DEPRECATED: This backend-specific image will be removed in a future release.
# Please use the standard image (cuda.Dockerfile / :latest-cuda) and set the GPU backend via an environment variable:
#   Ingest_Upscaler__PreferredGpuBackend=ROCm
# See docs/GPU_BACKEND_CONFIGURATION.md for details.
ARG BASE_IMAGE=ghcr.io/lolle2000la/manga-ingest-with-upscaling-remote-worker:latest-cuda
FROM ${BASE_IMAGE}
# Override the GPU backend for ROCm
ENV Ingest_Upscaler__PreferredGpuBackend=ROCm
# Signal to the application that this is a deprecated variant image so it can warn users
ENV Ingest_DeprecatedImageVariant=true
LABEL org.opencontainers.image.description="DEPRECATED: Use the standard image with Ingest_Upscaler__PreferredGpuBackend=ROCm. This image will be removed in a future release."
