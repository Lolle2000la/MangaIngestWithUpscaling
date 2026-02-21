# DEPRECATED: This remote-only image will be removed in a future release.
# Please use the standard image and set the remote-only mode via an environment variable:
#   Ingest_Upscaler__RemoteOnly=true
# See docs/REMOTE_ONLY_VARIANT.md for details.
ARG BASE_IMAGE=ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
FROM ${BASE_IMAGE}
# Enable remote-only execution mode - disables local upscaling
ENV Ingest_Upscaler__RemoteOnly=true
# Signal to the application that this is a deprecated variant image so it can warn users
ENV Ingest_DeprecatedImageVariant=true
LABEL org.opencontainers.image.description="DEPRECATED: Use the standard image with Ingest_Upscaler__RemoteOnly=true. This image will be removed in a future release."
