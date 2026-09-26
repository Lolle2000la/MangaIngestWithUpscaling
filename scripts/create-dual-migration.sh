#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <MigrationName>" >&2
  exit 1
fi

MIGRATION_NAME="$1"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Each provider keeps its own migration history and model snapshot, so a schema change has to be
# scaffolded once per provider. Data backfills must be written provider-specifically in the
# generated migration.
dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Sqlite" \
  --startup-project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Sqlite" \
  --context ApplicationDbContext \
  --output-dir Migrations

dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Postgres" \
  --startup-project "$REPO_ROOT/src/MangaIngestWithUpscaling.Data.Postgres" \
  --context ApplicationDbContext \
  --output-dir Migrations

echo "Created migrations named '$MIGRATION_NAME' for SQLite and PostgreSQL."
