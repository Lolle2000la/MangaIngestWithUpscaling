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
- Log timestamps are stored in **UTC** on both providers (the SQLite sink is configured with
  `storeTimestampInUtc: true`), so the logs UI's `.ToLocalTime()` renders them correctly.

## Provider-specific differences

The providers are equivalent for the application's features, with these deliberate differences:

- **Timestamp precision** — PostgreSQL `timestamp with time zone` is microsecond-precision while .NET
  `DateTime` uses 100-nanosecond ticks, so timestamps lose their sub-microsecond (7th) digit when
  migrated to PostgreSQL. Ordering and behavior are unaffected. See
  [Known limitations](./DATABASE_MIGRATION.md#known-limitations).
- **`Logs` non-nullable columns** — on a freshly created PostgreSQL `Logs` table `"Level"` and
  `"RenderedMessage"` are `NOT NULL`. Legacy or hand-edited rows containing `NULL` in either column
  would fail an `--include-logs` copy, whereas SQLite accepts them.
- **No automatic retry** — `EnableRetryOnFailure` is intentionally not enabled for PostgreSQL, because
  a retrying execution strategy rejects the user-initiated transactions several services open unless
  every transaction is wrapped in `CreateExecutionStrategy()`.
- **SQLite's historical column defaults** — columns added to the original SQLite database over time
  with `ALTER TABLE ADD COLUMN` keep a persistent column default (for example `PersistedTasks.Order`,
  `UpscalerProfiles.Deleted` and several timestamp columns). The fresh PostgreSQL baseline is created
  in one migration and has no such defaults. Raw SQL `INSERT`s that omit those columns therefore lean
  on SQLite's default but can fail or behave differently on PostgreSQL; use the EF/application APIs,
  which always supply values.
- **`Logs` schema and `search_path`** — the `Logs` table is pinned to the `public` schema, while the
  application tables are resolved through the connection's `search_path`. A non-default `search_path`
  is therefore unsupported: logs and application data would resolve against different schemas.
- **Concurrent startup migrations** — on PostgreSQL the startup migrations run under a session-level
  advisory lock, so several application replicas starting at once cannot race on
  `__EFMigrationsHistory` or apply the same DDL twice. SQLite's single-writer locking makes this
  unnecessary there.

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

Only the tests that obtain their database through `TestDatabaseFactory`/`TestDatabaseHelper` follow
this selection (an unrecognized `TEST_DB_PROVIDER` value throws rather than silently falling back to
SQLite). The remaining tests keep their own fixtures and are not provider-parameterized: most use EF
Core's InMemory provider, and a few use a raw SQLite connection directly.

The same applies to the UI test project, which swaps its in-memory database for the selected provider
so database-backed component tests run on both backends.

The PostgreSQL pass additionally runs the migration smoke tests (running `Database.Migrate()` against an
empty database on each provider) and the `DbMigrator` round-trip tests (SQLite ⇄ PostgreSQL, sequence
reset, log copying and ASP.NET Core Identity data). The identity test seeds a user/role/claim/login
through the framework's `UserManager`, migrates, and confirms the password still authenticates on the
new provider. Those tests require Docker and are skipped otherwise.
