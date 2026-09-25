# Database Providers

The application supports two relational databases:

- **SQLite** (default) — an embedded database file, no external service required.
- **PostgreSQL** — an external server, useful for larger installations or when the database
  should be shared/backed up separately.

Both providers expose the same logical schema and feature set; the active provider is chosen at
startup. The deliberate differences are listed under
[Provider-specific differences](#provider-specific-differences).

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

## Provider-specific differences

The providers are equivalent for the application's features, with these deliberate differences:

- **Timestamp precision** — PostgreSQL `timestamp with time zone` is microsecond-precision while .NET
  `DateTime` uses 100-nanosecond ticks, so timestamps lose their sub-microsecond (7th) digit when
  migrated to PostgreSQL. Ordering and behavior are unaffected. See
  [Known limitations](./DATABASE_MIGRATION.md#known-limitations).
- **No automatic retry** — `EnableRetryOnFailure` is intentionally not enabled for PostgreSQL, because
  a retrying execution strategy rejects the user-initiated transactions several services open unless
  every transaction is wrapped in `CreateExecutionStrategy()`.

## Migrating an existing installation

The `MangaIngestWithUpscaling.DbMigrator` tool copies an entire installation between providers in
either direction — Identity, libraries, manga, chapters, tasks, data-protection keys and (optionally)
logs. See the dedicated **[Database Migration Guide](./DATABASE_MIGRATION.md)** for the full runbook,
including stopping the application, backups, verification, switching configuration and rollback.

> The short version: stop the application, point the tool at the source and an empty target, run it,
> then set `DatabaseProvider` to the new backend and restart. The old database is left untouched as a
> fallback.

**Known limitation:** PostgreSQL `timestamp with time zone` has microsecond precision while .NET
`DateTime` uses 100-nanosecond ticks, so migrating to PostgreSQL truncates the sub-microsecond digit
of every timestamp (e.g. `…4581723` becomes `…458172`). This does not affect ordering or behavior.

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

Model/snapshot drift is also checked by a unit test
(`MigrationModelSnapshotTests.RuntimeModel_MatchesTheActiveProviderSnapshot`) on every run, so the
following commands are a convenient manual check:

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

The same applies to the UI test project, which swaps its in-memory database for the selected provider
so database-backed component tests run on both backends.

The PostgreSQL pass additionally runs the migration smoke tests (running `Database.Migrate()` against an
empty database on each provider) and the `DbMigrator` round-trip tests (SQLite ⇄ PostgreSQL, sequence
reset, log copying and ASP.NET Core Identity data). The identity test seeds a user/role/claim/login
through the framework's `UserManager`, migrates, and confirms the password still authenticates on the
new provider. Those tests require Docker and are skipped otherwise.
