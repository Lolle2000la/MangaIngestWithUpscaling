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
- Python 3.12 or newer
- Git with submodule support

### Bootstrap, Build, and Test the Repository

**CRITICAL TIMING EXPECTATIONS:**
- **NEVER CANCEL** any build or dependency installation commands
- **Build commands may take 30+ seconds** - always set timeout to 120+ seconds
- **First run may take 2-5 minutes** for Python environment setup

```bash
# 1. Install .NET 10.0 SDK (if not installed)
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet
export PATH="$HOME/.dotnet:$PATH"

# 2. Initialize git submodules (REQUIRED for build)
git submodule update --init --recursive

# 3. Restore dependencies (~24 seconds - NEVER CANCEL)
dotnet restore MangaIngestWithUpscaling.sln
# Build takes ~24s. Set timeout to 120+ seconds.

# 4. Build the solution (~23 seconds - NEVER CANCEL)  
dotnet build --no-restore MangaIngestWithUpscaling.sln
# Build takes ~23s. Set timeout to 120+ seconds.

# 5. Build Remote Worker separately (~1 second)
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

**Alternative Configuration (for full ML functionality):**
```bash
# Only use if you need local upscaling (will download PyTorch, takes 2-5 minutes)
export Ingest_Upscaler__UseCPU=true
export Ingest_Upscaler__RemoteOnly=false
dotnet run --project src/MangaIngestWithUpscaling
# FIRST RUN WILL TAKE 2-5 MINUTES - NEVER CANCEL
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
   dotnet csharpier format src/ test/ # Or even just the modified files
   # Only ever commit formatted code
   # Note that the submodule are rightly excluded from the glob pattern above
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
     dotnet test test/MangaIngestWithUpscaling.Tests --filter Category!=Download
     ```
     Don't run download tests unless you have a specific reason. They can take very long.
   - If you add new features or make changes that affect logic, consider writing new or updating existing tests.
   - Ensure tests pass before PR or merge.
   - Unless specified differently, tests should be written using xUnit v3, NSubstitute, and bUnit if testing Blazor components.

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

## Project Structure

### Key Directories
```
/
├── src/
│   ├── MangaIngestWithUpscaling/          # Main Blazor web application
│   ├── MangaIngestWithUpscaling.Shared/   # Shared library (models, services)
│   └── MangaIngestWithUpscaling.RemoteWorker/ # Remote upscaling worker
├── test/
│   ├── MangaIngestWithUpscaling.Tests/    # Unit tests for main app
│   ├── MangaIngestWithUpscaling.Shared.Tests/ # Unit tests for shared lib
│   ├── MangaIngestWithUpscaling.RemoteWorker.Tests/ # Unit tests for worker
│   └── MangaIngestWithUpscaling.Tests.UI/ # UI tests
├── MangaJaNaiConverterGui/            # Git submodule (ML backend files)
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
    "SelectedDeviceIndex": 0,
    "RemoteOnly": false,  // Set to true for development
    "PreferredGpuBackend": "Auto"
  }
}
```

### Environment Variables (for development)
- `Ingest_Upscaler__RemoteOnly=true` - Skip Python/ML setup
- `Ingest_Upscaler__UseCPU=true` - Force CPU backend
- `Ingest_ConnectionStrings__DefaultConnection` - Database path

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
dotnet csharpier check src/ test/

# Apply formatting fixes
dotnet csharpier format src/ test/
```

### Database Operations
The application uses SQLite with Entity Framework Core. Database migrations are applied automatically on startup.

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
     dotnet test test/MangaIngestWithUpscaling.Tests
     ```
   - Add or update tests if your change introduces or modifies features/logic.

## Troubleshooting

### Common Issues and Solutions

**"Could not find a suitable window platform" error:**
- This is expected in headless environments
- Application will fall back to CPU backend automatically
- Use `RemoteOnly=true` to skip GPU detection entirely

**Python environment setup timeout:**
- Network issues during PyTorch download
- Use `RemoteOnly=true` mode for development
- Allow 2-5 minutes for full ML environment setup

**Build timeout issues:**
- Always set timeouts to 120+ seconds for builds
- Never cancel long-running operations
- Submodule initialization is required before building

**Database migration errors:**
- Application creates SQLite databases automatically
- Check file permissions in working directory
- Migrations applied automatically on startup

### Performance Expectations
- **Restore**: ~24 seconds (NEVER CANCEL)
- **Build**: ~23 seconds (NEVER CANCEL) 
- **Remote Worker Build**: ~1 second
- **Application Startup**: ~10-30 seconds
- **First Run with ML**: 2-5 minutes (NEVER CANCEL)

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
dotnet test MangaIngestWithUpscaling.Tests
```

## Key Dependencies

- **.NET 10.0** - Required runtime and SDK
- **Blazor Server** - Web framework
- **MudBlazor** - UI component library  
- **Entity Framework Core** - Database ORM
- **SQLite** - Database engine
- **gRPC** - Communication protocol
- **Serilog** - Logging framework
- **Python 3.x + PyTorch** - ML backend (optional with RemoteOnly)

Remember: **ALWAYS use RemoteOnly mode for development** unless you specifically need to test ML functionality locally.
