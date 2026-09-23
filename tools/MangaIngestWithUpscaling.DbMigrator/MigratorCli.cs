using System.Globalization;
using MangaIngestWithUpscaling.Configuration;

namespace MangaIngestWithUpscaling.DbMigrator;

/// <summary>
/// Command-line entry point. The logic lives in <see cref="DataMigrator"/> so it can be tested; this
/// type only parses arguments and reports errors. It is a named class (not top-level statements) to
/// avoid generating a second global <c>Program</c> that would collide with the web application's.
/// </summary>
internal static class MigratorCli
{
    public static async Task<int> Main(string[] args)
    {
        Dictionary<string, string> options = ParseArgs(args);
        if (
            !options.TryGetValue("from", out string? fromProviderName)
            || !options.TryGetValue("to", out string? toProviderName)
            || !options.TryGetValue("from-connection", out string? fromConnection)
            || !options.TryGetValue("to-connection", out string? toConnection)
        )
        {
            PrintUsage();
            return 1;
        }

        if (
            !TryParseProvider(fromProviderName, out DatabaseProvider fromProvider)
            || !TryParseProvider(toProviderName, out DatabaseProvider toProvider)
        )
        {
            Console.Error.WriteLine("Unknown provider. Use 'sqlite' or 'postgres'.");
            return 1;
        }

        int batchSize =
            options.TryGetValue("batch-size", out string? batch)
            && int.TryParse(
                batch,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed
            )
            && parsed > 0
                ? parsed
                : 500;

        options.TryGetValue("from-logs-connection", out string? fromLogsConnection);
        options.TryGetValue("to-logs-connection", out string? toLogsConnection);

        var migratorOptions = new MigratorOptions(
            fromProvider,
            fromConnection,
            toProvider,
            toConnection,
            batchSize,
            options.ContainsKey("force"),
            options.ContainsKey("include-logs"),
            fromLogsConnection,
            toLogsConnection
        );

        try
        {
            await DataMigrator.MigrateAsync(
                migratorOptions,
                Console.WriteLine,
                CancellationToken.None
            );
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool TryParseProvider(string value, out DatabaseProvider provider)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "sqlite":
                provider = DatabaseProvider.Sqlite;
                return true;
            case "postgres":
            case "postgresql":
            case "npgsql":
                provider = DatabaseProvider.Postgres;
                return true;
            default:
                provider = DatabaseProvider.Sqlite;
                return false;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < input.Length; i++)
        {
            string arg = input[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string trimmed = arg[2..];
            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex >= 0)
            {
                result[trimmed[..equalsIndex]] = trimmed[(equalsIndex + 1)..];
                continue;
            }

            if (i + 1 < input.Length && !input[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[trimmed] = input[i + 1];
                i++;
            }
            else
            {
                result[trimmed] = "true";
            }
        }

        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tools/MangaIngestWithUpscaling.DbMigrator -- \\");
        Console.WriteLine("    --from <sqlite|postgres> --to <sqlite|postgres> \\");
        Console.WriteLine(
            "    --from-connection \"<connection string>\" --to-connection \"<connection string>\" \\"
        );
        Console.WriteLine("    [--batch-size 500] [--force] [--include-logs] \\");
        Console.WriteLine(
            "    [--from-logs-connection \"<sqlite logs connection>\"] [--to-logs-connection \"<sqlite logs connection>\"]"
        );
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine(
            "  - The target must be empty unless --force is passed (which clears it first)."
        );
        Console.WriteLine(
            "  - For SQLite, logs live in a separate file; pass the corresponding *-logs-connection."
        );
        Console.WriteLine(
            "  - Run the migration against a stopped application to avoid concurrent writes."
        );
    }
}
