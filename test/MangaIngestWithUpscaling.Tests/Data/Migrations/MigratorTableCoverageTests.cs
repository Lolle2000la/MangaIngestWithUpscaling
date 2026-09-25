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
        List<string> expected = IdentityKeyEntities(_source)
            .Select(entity => entity.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        List<string> actual = DataMigrator
            .PostgresIdentityTables.OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Every identity key the migrator resets must expose its value through a column literally named
    /// <c>Id</c>, because <see cref="DataMigrator" /> hard-codes that name in its
    /// <c>pg_get_serial_sequence('"Table"', 'Id')</c> reset SQL. A key stored in any other column
    /// would make the reset silently target a missing column and leave the sequence unadvanced.
    /// </summary>
    [Fact]
    public void PostgresIdentityKeys_UseTheIdColumnName()
    {
        List<string> offenders = IdentityKeyEntities(_source)
            .Select(entity =>
            {
                IProperty property = entity.FindPrimaryKey()!.Properties[0];
                return (
                    Table: entity.GetTableName()!,
                    Column: property.GetColumnName() ?? "<null>"
                );
            })
            .Where(pair => pair.Column != "Id")
            .Select(pair => $"{pair.Table}.{pair.Column}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The sequence reset SQL hard-codes the column name 'Id', but these identity keys use a "
                + $"different column: {string.Join(", ", offenders)}."
        );
    }

    /// <summary>
    /// The migrator copies tables in the returned order and clears them in reverse for <c>--force</c>,
    /// so parents must precede their children, or a clear or copy violates a foreign key. This
    /// inspects the model's foreign keys directly rather than trusting the hand-written list.
    /// </summary>
    [Fact]
    public void BuildTableOperations_OrdersParentsBeforeChildren()
    {
        List<string> order = DataMigrator
            .BuildTableOperations(
                _source,
                _target,
                batchSize: 500,
                _ => { },
                CancellationToken.None
            )
            .Select(operation => operation.Name)
            .ToList();

        List<string> duplicates = order
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            duplicates.Count == 0,
            $"The copy order repeats tables: {string.Join(", ", duplicates)}."
        );

        Dictionary<string, int> indexByTable = order
            .Select((name, index) => (name, index))
            .ToDictionary(entry => entry.name, entry => entry.index, StringComparer.Ordinal);

        List<string> violations = new();
        foreach (IEntityType entity in _source.Model.GetEntityTypes())
        {
            string? dependentTable = entity.GetTableName();
            if (dependentTable is null)
            {
                continue;
            }

            foreach (IForeignKey foreignKey in entity.GetForeignKeys())
            {
                string? principalTable = foreignKey.PrincipalEntityType.GetTableName();
                // Self-referencing keys (including owned types sharing the owner's table) copy
                // within a single table and impose no cross-table ordering.
                if (principalTable is null || principalTable == dependentTable)
                {
                    continue;
                }

                if (!indexByTable.TryGetValue(principalTable, out int principalIndex))
                {
                    violations.Add(
                        $"'{principalTable}' (principal of '{dependentTable}') is not copied"
                    );
                }
                else if (!indexByTable.TryGetValue(dependentTable, out int dependentIndex))
                {
                    violations.Add(
                        $"'{dependentTable}' (dependent of '{principalTable}') is not copied"
                    );
                }
                else if (principalIndex > dependentIndex)
                {
                    violations.Add(
                        $"'{principalTable}' (index {principalIndex}) is copied after its "
                            + $"dependent '{dependentTable}' (index {dependentIndex})"
                    );
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "BuildTableOperations must list parents before children: "
                + string.Join("; ", violations)
        );
    }

    /// <summary>
    /// The model's integer identity primary keys, matching the predicate the migrator's
    /// <see cref="DataMigrator.PostgresIdentityTables" /> list is guarded against.
    /// </summary>
    private static IEnumerable<IEntityType> IdentityKeyEntities(ApplicationDbContext context) =>
        context
            .Model.GetEntityTypes()
            .Where(entity =>
            {
                IKey? key = entity.FindPrimaryKey();
                return key is { Properties.Count: 1 }
                    && key.Properties[0].ClrType == typeof(int)
                    && key.Properties[0].ValueGenerated == ValueGenerated.OnAdd
                    && entity.GetTableName() is not null;
            });
}
