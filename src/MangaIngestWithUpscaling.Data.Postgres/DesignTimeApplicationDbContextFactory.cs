using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.BackgroundTaskQueue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MangaIngestWithUpscaling.Data.Postgres;

public sealed class DesignTimeApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        // Design-time factory used by EF CLI to build the model for Postgres migrations.
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=mangainjest_dev;Username=postgres;Password=postgres",
            b => b.MigrationsAssembly(typeof(PostgresMigrationsAssemblyMarker).Assembly.FullName)
        );

        // Ensure task polymorphic converters are registered for migration scaffolding.
        TaskJsonOptionsProvider.RegisterDerivedTypesFromAssemblies(typeof(BaseTask).Assembly);

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
