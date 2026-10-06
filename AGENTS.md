# MangaIngestWithUpscaling Development Instructions

**ALWAYS** reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.

## Overview

MangaIngestWithUpscaling is a **Blazor-based web application** designed to **ingest, process, and automatically upscale manga images**. The project consists of three main components:
- **Main Web Application** (MangaIngestWithUpscaling): Blazor server with MudBlazor UI
- **Shared Library** (MangaIngestWithUpscaling.Shared): Common services and models
- **Remote Worker** (MangaIngestWithUpscaling.RemoteWorker): Standalone upscaling worker for distributed processing

## Working Effectively

### Prerequisites
- .NET 10.0 SDK (REQUIRED - .NET 9/8 will not work)

### Bootstrap, Build, and Test the Repository

**CRITICAL TIMING EXPECTATIONS:**
- **NEVER CANCEL** any build or dependency installation commands
- **Build commands may take 30+ seconds** - always set timeout to 120+ seconds

```bash
# 1. Install .NET 10.0 SDK (if not installed)
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet
export PATH="$HOME/.dotnet:$PATH"

# 2. Restore dependencies (~24 seconds - NEVER CANCEL)
dotnet restore MangaIngestWithUpscaling.sln
# Build takes ~24s. Set timeout to 120+ seconds.

# 3. Build the solution (~23 seconds - NEVER CANCEL)  
dotnet build --no-restore MangaIngestWithUpscaling.sln
# Build takes ~23s. Set timeout to 120+ seconds.

# 4. Build Remote Worker separately (~1 second)
dotnet build --no-restore src/MangaIngestWithUpscaling.RemoteWorker
```

### Run the Applications

**Main Web Application (Development Mode):**
```bash
# ALWAYS use RemoteOnly mode for development to skip Python ML setup
export Ingest_Upscaler__RemoteOnly=true
dotnet run --project src/MangaIngestWithUpscaling
# Application starts on: http://localhost:5091 and https://localhost:7211
# NEVER CANCEL during startup - may take 30-60 seconds for database setup
```

**Remote Worker:**
```bash
dotnet run --project src/MangaIngestWithUpscaling.RemoteWorker
# Requires configuration in appsettings.json with ApiKey and ApiUrl
```

**Alternative Configuration (for local ML functionality):**
```bash
# Enable local ONNX upscaling
export Ingest_Upscaler__UseCPU=true
export Ingest_Upscaler__RemoteOnly=false
dotnet run --project src/MangaIngestWithUpscaling
```

## Validation

### ALWAYS run through these validation steps after making changes:

1. **Build Validation:**
   ```bash
   dotnet build --no-restore MangaIngestWithUpscaling.sln
   # Must complete without errors in ~23 seconds
   ```

2. **Format Code:**
   ```bash
   dotnet csharpier format src/ test/ tools/ # Or even just the modified files
   # Only ever commit formatted code
   ```

3. **Application Startup Test:**
   ```bash
   export Ingest_Upscaler__RemoteOnly=true
dotnet run --project src/MangaIngestWithUpscaling
   # Should start and show: "Now listening on: http://localhost:5091"
   ```

4. **Web Interface Test:**
   - Navigate to http://localhost:5091
   - Should see login page with navigation menu
   - Should be able to register new user account

5. **Remote Worker Build Test:**
   ```bash
   dotnet build --no-restore src/MangaIngestWithUpscaling.RemoteWorker
   # Should complete in ~1 second
   ```

6. **Test Suite (If you make logic or backend changes, or add features):**
   - **ALWAYS build and run tests if applicable and valuable for your changes.**
   - All test projects have "Tests" in their name.
   - To run all tests in the solution:
     ```bash
     dotnet test --solution MangaIngestWithUpscaling.sln --filter-not-trait Category=Download
     ```
     Don't run download tests unless you have a specific reason. They can take very long.
   - To also run the main test project against PostgreSQL (Testcontainers, Docker required):
     ```bash
     TEST_DB_PROVIDER=postgres dotnet test test/MangaIngestWithUpscaling.Tests/MangaIngestWithUpscaling.Tests.csproj --filter-not-trait Category=Download
     ```
   - To also run the UI test project against PostgreSQL (see [UI tests](#ui-tests-bunit)):
     ```bash
     TEST_DB_PROVIDER=postgres dotnet test test/MangaIngestWithUpscaling.Tests.UI/MangaIngestWithUpscaling.Tests.UI.csproj --filter-not-trait Category=Download
     ```
   - If you add new features or make changes that affect logic, consider writing new or updating existing tests.
   - Ensure tests pass before PR or merge.
   - Unless specified differently, tests should be written using xUnit v3, NSubstitute, and bUnit if testing Blazor components.

## UI tests (bUnit)

`MangaIngestWithUpscaling.Tests.UI` runs against SQLite by default and against PostgreSQL with
`TEST_DB_PROVIDER=postgres`. Each test class registers one `ApplicationDbContext` as a singleton so
the bUnit renderer and the test method share it.

**Known issue: the PostgreSQL pass can hang intermittently.** The run stops after the first skipped
test with no further output. A captured dump shows the test class's disposal blocked in
`DbContext.DisposeAsync` → `RelationalConnection.ResetStateAsync` → `NpgsqlConnection.CloseAsync` →
`NpgsqlDataReader.Close`, waiting for a PostgreSQL backend message: the shared context still has a
query in flight when the test disposes it. SQLite never hits this because closing its connection is
local and synchronous.

- Reproduces only on the PostgreSQL pass, and more readily on a low-core machine (~1 in 4 on CI,
  rarer on a fast one). Running the main PostgreSQL pass immediately before the UI pass, pinned to
  4 CPUs (`taskset -c 0-3`), reproduces it quickly.
- Diagnose by collecting a dump of the **test host** (not the `dotnet test` CLI) while it is stuck:
  ```bash
  dotnet-dump collect -p <host-pid> -o hang.dmp
  dotnet-dump analyze hang.dmp -c "dumpasync" -c "clrstack -all" -c "exit"
  ```
  The host process is
  `test/MangaIngestWithUpscaling.Tests.UI/bin/Debug/net10.0/MangaIngestWithUpscaling.Tests.UI`.
- Fixed in `TestDatabaseHelper.TestDbContext.DisposeAsync` by dropping the database **before**
  disposing the context: the drop terminates the context's backend connection, so the context close
  cannot block on the in-flight reader. Sharing one `DbContext` between the renderer and the test is
  still the underlying design smell — a per-operation context would remove the race entirely — but
  the drop-order change is sufficient and much smaller. Moving the teardown onto the thread pool does
  **not** address it; that was tried and the hang came back.

## Commenting Guidelines

- Write comments where they add real value: explain **why** (the intent, rationale, or non-obvious behavior), not **what** (which is clear from the code itself).
- **Avoid comments that restate the code.** For example, `// increment i by 1` for `i++` is unnecessary.
- Use XML documentation comments (`///`) for public methods, classes, and APIs to clarify purpose, expected usage, and edge cases.
- Add inline comments for complex logic, workarounds, or critical decisions that aren’t obvious from the code.
- **Remove or avoid autogenerated comments** that simply repeat code structure or parameter names.
- Prefer clarity in code over excessive explanation in comments—well-named variables, methods, and classes reduce the need for comments.
- **Keep comments up to date** as code changes; outdated comments are worse than none.

## Localization Guidelines

- **Location**: All resource files (`.resx`) MUST be placed in the `Resources` directory within the respective project (e.g., `src/MangaIngestWithUpscaling/Resources`, `src/MangaIngestWithUpscaling.Shared/Resources`).
- **Structure**: The directory structure within `Resources` MUST mirror the source structure precisely (e.g., `Components/Account/Pages/Manage/ApiKeys.razor` -> `Resources/Components/Account/Pages/Manage/ApiKeys.en-US.resx`).
- **Naming**: ALWAYS use full ISO culture codes (e.g., `en-US`, `de-DE`, `ja-JP`). NEVER use 2-letter language codes (e.g., `de`, `ja`).
- **File Format**: Ensure all `.resx` files use the standard .NET XML header structure.
- **Cleanup**: Never leave `.resx` files in the source directories alongside the code files.

## Entity Conventions

- **Automatic timestamps**: Entities whose `CreatedAt`/`ModifiedAt` should be managed by `ApplicationDbContext.UpdateTimestamps` must implement `IHasCreatedAt` / `IHasModifiedAt` (`src/MangaIngestWithUpscaling.Shared/Data/Abstractions`). Entities whose timestamps are set manually must **not** implement them.
- **Library configuration**: Child entities of a `Library` that represent configuration (ingest paths, filter rules, rename rules) implement `ILibraryConfiguration` so that adding, updating or deleting one bumps the owning `Library.ModifiedAt`.

## Blazor + EF Core

- Never enumerate a `DbSet`/`IQueryable` directly in a render tree (e.g.
  `@foreach (var library in DbContext.Libraries)`). That runs a synchronous query during render, and
  on PostgreSQL it collides with any other query in flight on the same scoped `ApplicationDbContext`
  ("A second operation was started on this context instance before a previous operation completed").
  Materialize the data first in `OnInitializedAsync`/`OnParametersSetAsync` (or an async `MudTable`
  `ServerData`) and render from the result. `MudSelect` dropdowns are the usual offender.
- Never run a synchronous EF query from a property read in markup (e.g.
  `UserManager.Users.Any()`); compute it in an async lifecycle method instead.

## Logging schema

- The `Logs` table is **not** managed by EF migrations; the `Log` entity (`src/MangaIngestWithUpscaling.Data/LogModel/Log.cs`) is the source of truth for its shape.
- Adding, renaming or changing the nullability of a `Log` property requires updating `PostgresLogging.CreateTableSql`. For SQLite the table is created by the external `Serilog.Sinks.SQLite` sink; a guard test (`SqliteLoggingSinkTests`) pins the sink schema to the model.
- Log timestamps are stored in **UTC** on both providers; the SQLite sink is configured with `storeTimestampInUtc: true` so the UI can render them in the browser's time zone.
- The migrator copies tables and resets PostgreSQL sequences from hand-maintained lists in `DataMigrator`; update them when entities change. `MigratorTableCoverageTests` fails until they match the EF model.

## Project Structure

### Key Directories
```
/
├── src/
│   ├── MangaIngestWithUpscaling/          # Main Blazor web application
│   ├── MangaIngestWithUpscaling.Shared/   # Shared library (models, services)
│   ├── MangaIngestWithUpscaling.Data/     # EF Core entities, DbContexts, task queries
│   ├── MangaIngestWithUpscaling.Data.Sqlite/   # SQLite migrations
│   ├── MangaIngestWithUpscaling.Data.Postgres/ # PostgreSQL migrations
│   └── MangaIngestWithUpscaling.RemoteWorker/ # Remote upscaling worker
├── test/
│   ├── MangaIngestWithUpscaling.Tests/    # Unit tests for main app (dual-provider)
│   ├── MangaIngestWithUpscaling.Shared.Tests/ # Unit tests for shared lib
│   ├── MangaIngestWithUpscaling.RemoteWorker.Tests/ # Unit tests for worker
│   └── MangaIngestWithUpscaling.Tests.UI/ # UI tests
├── tools/
│   └── MangaIngestWithUpscaling.DbMigrator/ # SQLite <-> PostgreSQL data migration CLI
├── docs/                              # Documentation
└── .github/workflows/                 # CI/CD pipelines
```

### Important Files
- `MangaIngestWithUpscaling.sln` - Main solution file
- `.editorconfig` - Code formatting rules (comprehensive C# styling)
- `appsettings.json` - Application configuration
- `docker-compose.yml` - Container deployment configuration

## Configuration

### Development Configuration (appsettings.json)
```json
{
  "Upscaler": {
    "UseFp16": true,
    "UseCPU": false,
    "SelectedDeviceIndex": 1,
    "RemoteOnly": false,  // Set to true for development
    "PreferredGpuBackend": "Auto"
  }
}
```

### Environment Variables (for development)
- `Ingest_Upscaler__RemoteOnly=true` - Skip Python/ML setup
- `Ingest_Upscaler__UseCPU=true` - Force CPU backend
- `Ingest_DatabaseProvider=Postgres` - Select the database backend (`Sqlite` is the default; also accepts `sqlite3`, `postgres`, `postgresql`, `npgsql`; an unknown value fails startup instead of silently falling back)
- `Ingest_ConnectionStrings__DefaultConnection` - SQLite database path (SQLite only)
- `Ingest_ConnectionStrings__PostgresConnection=Host=…;Database=…;Username=…;Password=…` - Required when `Ingest_DatabaseProvider=Postgres`
- `Ingest_ConnectionStrings__LoggingConnection` - SQLite logs file (SQLite only; on PostgreSQL the logs live in the application database)

## Common Tasks

### Building Different Components
```bash
# Full solution build
dotnet build MangaIngestWithUpscaling.sln

# Individual projects
dotnet build src/MangaIngestWithUpscaling/
dotnet build src/MangaIngestWithUpscaling.RemoteWorker/
dotnet build src/MangaIngestWithUpscaling.Shared/
```

### Code Formatting
```bash
# Check formatting without changes
dotnet csharpier check src/ test/ tools/

# Apply formatting fixes
dotnet csharpier format src/ test/ tools/
```

### Database Operations
The application supports SQLite (default) and PostgreSQL with Entity Framework Core. Migrations are applied automatically on startup. See [Database Providers](./docs/DATABASE_PROVIDERS.md) for configuration and the SQLite ⇄ PostgreSQL migration tool.

Each provider owns its migrations in a separate assembly (`MangaIngestWithUpscaling.Data.Sqlite` / `MangaIngestWithUpscaling.Data.Postgres`). Scaffold a schema change for both providers with:

```bash
./scripts/create-dual-migration.sh <Name>
```

To verify a single provider's model and snapshot agree:

```bash
dotnet ef migrations has-pending-model-changes --project src/MangaIngestWithUpscaling.Data.Sqlite --startup-project src/MangaIngestWithUpscaling.Data.Sqlite --context ApplicationDbContext
dotnet ef migrations has-pending-model-changes --project src/MangaIngestWithUpscaling.Data.Postgres --startup-project src/MangaIngestWithUpscaling.Data.Postgres --context ApplicationDbContext
```

- Review generated migrations before committing. Data backfills must run **before** a column is dropped (see `AddMultipleIngestPaths`), and migrations that transform data should get a regression test that migrates to the previous migration, seeds old-schema data, then migrates forward (see `AddMultipleIngestPathsMigrationTests`).
- Data-backfill migrations must use provider-specific SQL (SQLite's `json_each` vs PostgreSQL's `jsonb_array_elements_text`).
- An "EF tools version is older than the runtime" warning is expected and harmless.

### Testing Scenarios

**After making changes, ALWAYS test these scenarios:**

1. **Fresh Build Test:**
   ```bash
   git clean -xdf # Cleans all untracked files including bin/ and obj/ folders
dotnet restore && dotnet build
   ```

2. **Application Startup:**
   ```bash
   export Ingest_Upscaler__RemoteOnly=true
dotnet run --project src/MangaIngestWithUpscaling
   # Wait for "Now listening on:" message
   ```

3. **User Registration Flow:**
   - Go to http://localhost:5091
   - Click "Register as a new user"
   - Create account and verify login works

4. **Remote Worker Communication:**
   - Build and run both main app and remote worker
   - Verify gRPC communication (requires API key setup)

5. **Test Suite (If you made relevant code changes):**
   - Run tests using:
     ```bash
     dotnet test --solution MangaIngestWithUpscaling.sln --filter-not-trait Category=Download
     ```
   - Add or update tests if your change introduces or modifies features/logic.

## Troubleshooting

### Common Issues and Solutions

**"Could not find a suitable window platform" error:**
- This is expected in headless environments
- Application will fall back to CPU backend automatically
- Use `RemoteOnly=true` to skip GPU detection entirely

**Build timeout issues:**
- Always set timeouts to 120+ seconds for builds
- Never cancel long-running operations

**Database migration errors:**
- Application creates SQLite databases automatically
- Check file permissions in working directory
- Migrations applied automatically on startup

### Performance Expectations
- **Restore**: ~24 seconds (NEVER CANCEL)
- **Build**: ~23 seconds (NEVER CANCEL) 
- **Remote Worker Build**: ~1 second
- **Application Startup**: ~10-30 seconds
- **First Run with ML**: Models downloaded automatically on first start (~30-60s)

## CI/CD Information

The project uses GitHub Actions for continuous integration:
- `.github/workflows/dotnet.yml` - Build validation for PRs
- `.github/workflows/*-main.yml` - Main branch builds
- `.github/workflows/*-release.yml` - Release builds

**Always ensure your changes pass:**
```bash
dotnet restore MangaIngestWithUpscaling.sln
dotnet build --no-restore MangaIngestWithUpscaling.sln /p:TreatWarningsAsErrors=true
# Run tests if relevant changes are made
dotnet test --solution MangaIngestWithUpscaling.sln --filter-not-trait Category=Download
```

CI (`.github/workflows/dotnet.yml`) runs **three** test passes: unit tests on SQLite, the main test
project against PostgreSQL, and the UI test project against PostgreSQL (the last two via
Testcontainers, so Docker is required). A change that passes locally on SQLite can still fail CI, so
run the two `TEST_DB_PROVIDER=postgres` commands above when your change touches data access.

## Key Dependencies

- **.NET 10.0** - Required runtime and SDK
- **Blazor Server** - Web framework
- **MudBlazor** - UI component library  
- **Entity Framework Core** - Database ORM
- **SQLite / PostgreSQL (Npgsql)** - Supported database engines (SQLite is the default)
- **Testcontainers** - Spins up PostgreSQL for the dual-provider test passes (Docker required)
- **gRPC** - Communication protocol
- **Serilog** - Logging framework
- **Microsoft.ML.OnnxRuntime** - Pure C# ML inference engine (MIGraphX, DirectML, CUDA, CPU)

Remember: **ALWAYS use RemoteOnly mode for development** unless you specifically need to test ML functionality locally.
