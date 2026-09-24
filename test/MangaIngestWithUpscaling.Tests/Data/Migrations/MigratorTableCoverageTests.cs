using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.DbMigrator;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MangaIngestWithUpscaling.Tests.Data.Migrations;

/// <summary>
/// Guards the migrator's hand-maintained table lists against the EF model. The migrator copies and
/// resets sequences by explicit table name, so an entity added without updating those lists would be
/// silently dropped (or its ids would collide) when switching providers. These tests fail until the
/// migrator is updated.
/// </summary>
public class MigratorTableCoverageTests : IAsyncDisposable
{
    private readonly TestDatabase _database = TestDatabaseFactory.Create();
    private readonly ApplicationDbContext _source;
    private readonly ApplicationDbContext _target;

    public MigratorTableCoverageTests()
    {
        _source = _database.CreateContext();
        _target = _database.CreateContext();
    }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public void BuildTableOperations_CoversEveryModelTable()
    {
        List<string> copied = DataMigrator
            .BuildTableOperations(
                _source,
                _target,
                batchSize: 500,
                _ => { },
                CancellationToken.None
            )
            .Select(operation => operation.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        List<string> mapped = _source
            .Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(mapped, copied);
    }

    [Fact]
    public void PostgresIdentityTables_MatchTheModelIdentityKeys()
    {
        List<string> expected = _source
            .Model.GetEntityTypes()
            .Where(entity =>
            {
                IKey? key = entity.FindPrimaryKey();
                return key is { Properties.Count: 1 }
                    && key.Properties[0].ClrType == typeof(int)
                    && key.Properties[0].ValueGenerated == ValueGenerated.OnAdd
                    && entity.GetTableName() is not null;
            })
            .Select(entity => entity.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        List<string> actual = DataMigrator
            .PostgresIdentityTables.OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);
    }
}
