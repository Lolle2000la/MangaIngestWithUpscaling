# Database Providers

The application supports two relational databases:

- **SQLite** (default) — an embedded database file, no external service required.
- **PostgreSQL** — an external server, useful for larger installations or when the database
  should be shared/backed up separately.

Both providers share the same schema and feature set. The active provider is chosen at startup.

## Configuration

| Setting | Description | Default |
|---|---|---|
| `DatabaseProvider` | `Sqlite` or `Postgres`. | `Sqlite` |
| `ConnectionStrings:DefaultConnection` | The SQLite database file. | `Data Source=data.db;Mode=ReadWriteCreate` |
| `ConnectionStrings:PostgresConnection` | The PostgreSQL connection string. Required when `DatabaseProvider` is `Postgres`. | – |
| `ConnectionStrings:LoggingConnection` | The SQLite log database file. Only used when `DatabaseProvider` is `Sqlite`. | `Data Source=logs.db` |

Environment variables (the `Ingest_` prefix maps to configuration):

```bash
Ingest_DatabaseProvider=Postgres
Ingest_ConnectionStrings__PostgresConnection="Host=postgres;Database=manga_ingest;Username=postgres;Password=postgres"
```

The database schema is migrated automatically on startup.

## Logs

- On **SQLite**, logs are written to the separate `LoggingConnection` file (as before).
- On **PostgreSQL**, logs are written to a `Logs` table in the application database. The table is
  created automatically on startup.

## Migrating an existing installation

The `MangaIngestWithUpscaling.DbMigrator` tool copies an entire installation between providers in
either direction. It applies the target schema, then copies all application tables (Identity,
libraries, manga, chapters, tasks, data protection keys). Existing logs can optionally be copied.

> Run the migration while the application is **stopped** to avoid concurrent writes.

```bash
# SQLite -> PostgreSQL
dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- \
  --from sqlite --to postgres \
  --from-connection "Data Source=/data/data.db" \
  --to-connection "Host=postgres;Database=manga_ingest;Username=postgres;Password=postgres"

# PostgreSQL -> SQLite
dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- \
  --from postgres --to sqlite \
  --from-connection "Host=postgres;Database=manga_ingest;Username=postgres;Password=postgres" \
  --to-connection "Data Source=/data/data.db"
```

Options:

| Option | Description |
|---|---|
| `--batch-size <n>` | Rows copied per batch (default `500`). |
| `--force` | Clears a non-empty target before copying. Without it the tool refuses to overwrite. |
| `--include-logs` | Also copies the `Logs` table. |
| `--from-logs-connection` | The SQLite logs file to read when the source is SQLite and `--include-logs` is set. |
| `--to-logs-connection` | The SQLite logs file to write when the target is SQLite and `--include-logs` is set. |

After a successful migration, point `DatabaseProvider` at the new backend and restart the
application. The old database is left untouched as a fallback.

## Docker

`docker-compose.yml` includes a `postgres` service behind the `postgres` profile. To run the
application against PostgreSQL, uncomment the two `Ingest_DatabaseProvider` /
`Ingest_ConnectionStrings__PostgresConnection` environment variables on the application service and
start both:

```bash
docker compose --profile postgres up
```

## Developing migrations

Each provider keeps its own migration history and model snapshot in its own assembly
(`MangaIngestWithUpscaling.Data.Sqlite` / `MangaIngestWithUpscaling.Data.Postgres`). Scaffold a
schema change for both providers with:

```bash
./scripts/create-dual-migration.sh <MigrationName>
```

Migrations that transform data must use provider-specific SQL (for example SQLite's `json_each`
versus PostgreSQL's `jsonb_array_elements_text`). The task-payload queries already encapsulate this
in `PersistedTaskQueries`.

To verify that the model and snapshots agree:

```bash
dotnet ef migrations has-pending-model-changes \
  --project src/MangaIngestWithUpscaling.Data.Sqlite \
  --startup-project src/MangaIngestWithUpscaling.Data.Sqlite \
  --context ApplicationDbContext

dotnet ef migrations has-pending-model-changes \
  --project src/MangaIngestWithUpscaling.Data.Postgres \
  --startup-project src/MangaIngestWithUpscaling.Data.Postgres \
  --context ApplicationDbContext
```

## Testing against PostgreSQL

The unit tests run against SQLite by default. Set `TEST_DB_PROVIDER=postgres` to run them against a
throwaway PostgreSQL database started with Testcontainers (Docker required):

```bash
TEST_DB_PROVIDER=postgres dotnet test test/MangaIngestWithUpscaling.Tests/MangaIngestWithUpscaling.Tests.csproj
```
