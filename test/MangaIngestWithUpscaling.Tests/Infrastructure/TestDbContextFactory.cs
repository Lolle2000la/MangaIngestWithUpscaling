using MangaIngestWithUpscaling.Data;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Tests.Infrastructure;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> backed by a single <see cref="TestDatabase"/>.
/// Services that create a context per operation receive a fresh context connected to the same
/// isolated database as the test's own context, so committed state is visible to both.
/// </summary>
public sealed class TestDbContextFactory(TestDatabase database)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => database.CreateContext();
}
