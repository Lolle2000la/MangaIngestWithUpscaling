# Database Migration Guide (SQLite ⇄ PostgreSQL)

This guide walks through moving an existing installation between the embedded SQLite database and
an external PostgreSQL server, in either direction, using the bundled
`MangaIngestWithUpscaling.DbMigrator` tool.

The migration is **additive and reversible**: the tool only reads the source and writes a new target
database, so your existing `data.db`/`logs.db` are left untouched and can be used as a fallback.

For provider configuration and development details, see [Database Providers](./DATABASE_PROVIDERS.md).

## When to migrate

- You are outgrowing the single-file SQLite database (many concurrent writers, larger libraries).
- You already run PostgreSQL and want the application data consolidated there.
- You want to move back from PostgreSQL to SQLite.

## What the tool does

1. Applies the target provider's schema migrations to the target database.
2. Refuses to run if the target already has data (unless `--force` is given, which clears it first).
3. Streams every application table across — Identity (users, roles, claims, logins, user-roles and
   tokens), libraries and their ingest paths/filter/rename rules, upscaler profiles, manga and
   alternative titles, chapters, merged-chapter info, filtered images, split state, strip findings,
   tasks, API keys and data-protection keys. (In short: every table in the application model.)
4. Optionally copies the `Logs` table (`--include-logs`). If the target log table already has rows
   the tool refuses to overwrite it, unless `--force` is given to clear it first.
5. Resets the PostgreSQL identity sequences (application tables and `Logs`) so the next inserted row
   cannot collide with a copied id.

> **The target's schema is migrated before the empty-target check.** Step 1 runs to completion
> before step 2 inspects the target, so pointing the tool at an existing database (for example one
> from an older application version) will apply any pending migrations to it and only then refuse.
> Migrations alone change the schema, not your rows, but use a fresh database to be safe.

## Prerequisites

- The **source database must be schema-current**: the tool reads the source through the current
  application model, so run the current version of the app once against the source (or otherwise
  ensure its migrations are applied) before migrating. Otherwise an operator who upgrades and runs
  the migrator before ever booting the new version can hit `column does not exist` errors.
- The application is **stopped** (see [Stopping the application](#1-stop-the-application)).
- A reachable PostgreSQL database and a user that may run DDL — the tool and the app create the
  schema. The database itself must already exist (the tool does not create it).
- A way to run the tool: a checkout with the .NET 10 SDK, or a published/self-contained build.

## Forward: SQLite → PostgreSQL

### 1. Stop the application

```bash
docker compose stop mangaingestwithupscaling     # Docker
# or Ctrl+C / systemctl stop <service>            # bare dotnet / systemd
```

The tool has no locking; concurrent writes from a running application can be lost.

### 2. Back up the SQLite files

```bash
cp -a /path/to/data /path/to/data.backup-$(date +%F)
```

Back up while the application is stopped: in WAL mode a raw copy of `data.db` can miss
un-checkpointed writes. Alternatively use `sqlite3 data.db ".backup backup.db"`.

### 3. Create an empty PostgreSQL database

With the bundled compose service:

```bash
docker compose --profile postgres up -d postgres
docker exec -it <postgres-container> psql -U postgres \
  -c "CREATE USER manga WITH PASSWORD 'change-me';" \
  -c "CREATE DATABASE manga_ingest OWNER manga;"
```

Or against your own server, create a dedicated database (and ideally a dedicated, non-superuser
account that owns it).

> The bundled `postgres` service has a health check, and the application waits for it before starting
> (an optional `depends_on`), so a slow first boot no longer races the database.

### 4. Run the migrator

Run the tool somewhere that can read the SQLite files **and** reach PostgreSQL.

**From a checkout (host):**

```bash
dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- \
  --from sqlite --to postgres \
  --from-connection "Data Source=/path/to/data/data.db" \
  --from-logs-connection "Data Source=/path/to/data/logs.db" \
  --to-connection "Host=localhost;Port=5432;Database=manga_ingest;Username=manga;Password=change-me" \
  --include-logs
```

**As a published binary (no SDK required on the server):**

```bash
dotnet publish tools/MangaIngestWithUpscaling.DbMigrator -c Release -r linux-x64 \
  --self-contained -o ./migrator

./migrator/MangaIngestWithUpscaling.DbMigrator \
  --from sqlite --to postgres \
  --from-connection "Data Source=/path/to/data/data.db" \
  --from-logs-connection "Data Source=/path/to/data/logs.db" \
  --to-connection "Host=localhost;Port=5432;Database=manga_ingest;Username=manga;Password=change-me" \
  --include-logs
```

**From inside the application image (standard and all variant images):**

```bash
docker compose --profile postgres run --rm --entrypoint dotnet mangaingestwithupscaling \
  MangaIngestWithUpscaling.DbMigrator.dll \
  --from sqlite --to postgres \
  --from-connection "Data Source=/data/data.db" \
  --from-logs-connection "Data Source=/data/logs.db" \
  --to-connection "Host=postgres;Database=manga_ingest;Username=manga;Password=change-me" \
  --include-logs
```

`--entrypoint dotnet` overrides the image's application entrypoint. The tool ships in the standard
image and every variant image (they are all built `FROM` it) and is version-matched to the app that
produced them, so no separate download or SDK is needed. Stop the application first (see
[Stopping the application](#1-stop-the-application)); `docker compose run` starts a one-off container
that mounts the same `/data` volume, so the SQLite files are read in place. The `--profile postgres`
above is only needed for the bundled Compose server; drop it when the target is an external
PostgreSQL, and adjust the host and the volume path (`/data` by default) to match where you run it.

The tool prints per-table progress and finishes with `Migration completed successfully.`.

> **Connections differ by vantage point.** Run on the host, PostgreSQL is usually
> `Host=localhost`; the application running inside Docker Compose reaches it as `Host=postgres`
> (the service name). Use whichever is correct for where the command runs.

### 5. Verify

Spot-check row counts (they should equal the source):

```bash
docker exec -i <postgres-container> psql -U manga -d manga_ingest -c \
  "SELECT 'MangaSeries', count(*) FROM \"MangaSeries\"
   UNION ALL SELECT 'Chapters', count(*) FROM \"Chapters\"
   UNION ALL SELECT 'Logs', count(*) FROM \"Logs\";"
```

Optionally verify there are no orphaned rows:

```bash
docker exec -i <postgres-container> psql -U manga -d manga_ingest -c \
  "SELECT count(*) AS orphan_chapters FROM \"Chapters\" c
   WHERE NOT EXISTS (SELECT 1 FROM \"MangaSeries\" m WHERE m.\"Id\" = c.\"MangaId\");"
```

### 6. Point the application at PostgreSQL and start it

Docker Compose — add to the application service:

```yaml
environment:
  - Ingest_DatabaseProvider=Postgres
  - Ingest_ConnectionStrings__PostgresConnection=Host=postgres;Database=manga_ingest;Username=manga;Password=change-me
```

```bash
docker compose up -d
curl http://localhost:8080/health          # expect: 200 Healthy
```

Bare `dotnet` / systemd — set the same two values via environment variables or `appsettings.json`:

```json
{
  "DatabaseProvider": "Postgres",
  "ConnectionStrings": {
    "PostgresConnection": "Host=localhost;Database=manga_ingest;Username=manga;Password=change-me"
  }
}
```

On startup the application applies migrations (a no-op after the tool ran) and ensures the `Logs`
table exists.

## Reverse: PostgreSQL → SQLite

Stop the application, then migrate into **new** file paths so the live files are never overwritten:

```bash
dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- \
  --from postgres --to sqlite \
  --from-connection "Host=localhost;Port=5432;Database=manga_ingest;Username=manga;Password=change-me" \
  --to-connection "Data Source=/path/to/data/new-data.db" \
  --include-logs \
  --to-logs-connection "Data Source=/path/to/data/new-logs.db"
```

Then either point `DefaultConnection`/`LoggingConnection` at the new files, or — after backing up —
rename them into place:

```bash
mv /path/to/data/data.db      /path/to/data/data.db.old
mv /path/to/data/new-data.db  /path/to/data/data.db
mv /path/to/data/logs.db      /path/to/data/logs.db.old
mv /path/to/data/new-logs.db  /path/to/data/logs.db
```

> **Check before you rename.** `mv` overwrites silently: make sure a previous
> `data.db.old`/`logs.db.old` is not clobbered (choose a dated name if in doubt). If the application
> ever ran with WAL enabled, also remove or move the stale `data.db-wal` / `data.db-shm` (and the
> `logs.db-*` equivalents) sidecars before starting — a leftover WAL from the old database can be
> replayed on top of the newly copied file and corrupt it.

Set `DatabaseProvider` back to `Sqlite` (or remove the override; `Sqlite` is the default) and start
the application.

## Roll back

Because the migration does not modify the source, rolling back is just configuration:

1. Stop the application.
2. Set `DatabaseProvider` back to the previous value (or remove it; `Sqlite` is the default) and
   restore the previous connection settings.
3. Start the application.

The original database files are still in place. If you migrated to PostgreSQL and have been running
on it since, migrate back with the [reverse](#reverse-postgresql--sqlite) procedure first.

## Options reference

| Option | Description |
|---|---|
| `--from <sqlite\|postgres>` | Source provider. |
| `--to <sqlite\|postgres>` | Target provider. |
| `--from-connection <cs>` | Source connection string. |
| `--to-connection <cs>` | Target connection string. |
| `--batch-size <n>` | Rows inserted per batch (default `500`). |
| `--force` | Clear a non-empty target before copying, including the `Logs` table when `--include-logs` is used. The application tables are cleared **before any data is copied**, so if the run then fails the target is left empty and is unusable until a successful re-run. The target `Logs` table is cleared later, inside the log-copy step, and only when there is a source logs store to copy from — if the source logs store is missing or unavailable, the target `Logs` table is left untouched even with `--force`. Without it the tool refuses to overwrite. |
| `--include-logs` | Also copy the `Logs` table. If the source has no `Logs` table (a missing SQLite logs file, or a PostgreSQL source that never logged) the copy is skipped with a message rather than failing. |
| `--from-logs-connection <cs>` | SQLite logs file to read (required for a SQLite source with `--include-logs`). |
| `--to-logs-connection <cs>` | SQLite logs file to write (required for a SQLite target with `--include-logs`). |

## Troubleshooting

- **`Target table '…' is not empty`** — the target already contains data. Use a fresh database, or
  pass `--force` to clear it.
- **`Target table 'Logs' is not empty`** — the same guard, for `--include-logs`. Pass `--force` to
  clear the target log table before the copy.
- **`The 'from-logs-connection'/'to-logs-connection' option is required …`** — with `--include-logs`
  and a SQLite endpoint you must point at its separate `logs.db` file.
- **`Cannot write DateTime with Kind=Unspecified …`** — should not occur; the tool normalizes
  SQLite's kind-less timestamps to UTC. Please report it.
- **Connection errors** — check the connection string for the machine running the tool
  (`localhost` on the host vs. `postgres` inside Compose), and that the database exists.
- **A run failed part-way through** — the target may be partially populated. Fix the cause, then
  re-run with `--force`: without it the tool refuses to write into the non-empty target.
- The tool prints the full exception chain on failure, including the provider's inner error.

## Known limitations

- **Timestamp precision:** PostgreSQL `timestamp with time zone` is microsecond-precision while .NET
  `DateTime` uses 100-nanosecond ticks, so migrating to PostgreSQL truncates the sub-microsecond
  digit (e.g. `…4581723` becomes `…458172`). This does not affect ordering or behavior.
- **Logs are optional and not authoritative** — they are copied only with `--include-logs`.
- **The data-protection key ring is stored unencrypted** — the tool copies the `DataProtectionKeys`
  rows. Because the application calls `PersistKeysToDbContext` without a `ProtectKeysWith*` method,
  ASP.NET Core disables the default encryption-at-rest mechanism, so the keys are stored unencrypted
  and travel with the database. Existing auth cookies, antiforgery tokens and API-key sessions keep
  working after migrating to a new machine or container, as long as `SetApplicationName` is
  unchanged. If an operator later adds `ProtectKeysWithDpapi()` (Windows) or
  `ProtectKeysWithCertificate(...)`, the ring becomes bound to that machine or secret and copying
  only the database is no longer sufficient.
