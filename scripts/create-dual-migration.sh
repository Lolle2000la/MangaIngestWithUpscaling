#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <MigrationName>" >&2
  exit 1
fi

MIGRATION_NAME="$1"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Generate Sqlite migration using the web app as the startup project.
dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Sqlite" \
  --startup-project "$REPO_ROOT/src/MangaIngestWithUpscaling" \
  --context ApplicationDbContext \
  --output-dir Migrations

# Generate Postgres migration using the Postgres data project as both target and startup (uses design-time factory).
dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Postgres" \
  --startup-project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Postgres" \
  --context ApplicationDbContext \
  --output-dir Migrations

echo "Created migrations named '$MIGRATION_NAME' for Sqlite and Postgres."