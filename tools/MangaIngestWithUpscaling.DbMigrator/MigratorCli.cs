using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using MangaIngestWithUpscaling.Configuration;

namespace MangaIngestWithUpscaling.DbMigrator;

/// <summary>
/// Command-line entry point. The logic lives in <see cref="DataMigrator"/> so it can be tested; this
/// type only builds the command, parses arguments and reports errors. It is a named class (not
/// top-level statements) to avoid generating a second global <c>Program</c> that would collide with
/// the web application's.
/// </summary>
internal static class MigratorCli
{
    public static Task<int> Main(string[] args) => RunAsync(args, DataMigrator.MigrateAsync);

    /// <summary>
    /// The CLI body, with the migration call injected so argument handling and exit codes can be
    /// tested without a database.
    /// </summary>
    internal static Task<int> RunAsync(
        string[] args,
        Func<MigratorOptions, Action<string>, CancellationToken, Task> migrateAsync
    ) => RunAsync(args, migrateAsync, Console.Out, Console.Error);

    /// <summary>
    /// As <see cref="RunAsync(string[], Func{MigratorOptions, Action{string}, CancellationToken, Task})"/>,
    /// but writing to the supplied writers so tests can capture help and error output.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        Func<MigratorOptions, Action<string>, CancellationToken, Task> migrateAsync,
        TextWriter output,
        TextWriter error
    )
    {
        RootCommand root = BuildCommand(migrateAsync);
        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            // Exceptions are handled inside the action so connection strings can be redacted first.
            EnableDefaultExceptionHandler = false,
        };

        return await root.Parse(args).InvokeAsync(configuration, CancellationToken.None);
    }

    private static RootCommand BuildCommand(
        Func<MigratorOptions, Action<string>, CancellationToken, Task> migrateAsync
    )
    {
        var fromOption = new Option<DatabaseProvider>("--from")
        {
            Description = "Source provider: 'sqlite' or 'postgres'.",
            HelpName = "provider",
            Required = true,
            CustomParser = ParseProvider,
        };
        var toOption = new Option<DatabaseProvider>("--to")
        {
            Description = "Target provider: 'sqlite' or 'postgres'.",
            HelpName = "provider",
            Required = true,
            CustomParser = ParseProvider,
        };
        var fromConnectionOption = new Option<string>("--from-connection")
        {
            Description = "Source connection string.",
            Required = true,
        };
        var toConnectionOption = new Option<string>("--to-connection")
        {
            Description = "Target connection string.",
            Required = true,
        };
        var batchSizeOption = new Option<int>("--batch-size")
        {
            Description = "Rows inserted per batch (default 500).",
            DefaultValueFactory = _ => 500,
            Validators =
            {
                result =>
                {
                    if (result.GetValueOrDefault<int>() <= 0)
                    {
                        result.AddError("--batch-size must be greater than zero.");
                    }
                },
            },
        };
        var forceOption = new Option<bool>("--force")
        {
            Description =
                "Clear a non-empty target before copying. Without it the tool refuses to overwrite.",
        };
        var includeLogsOption = new Option<bool>("--include-logs")
        {
            Description =
                "Also copy the Logs table. Skipped with a message when the source has no logs.",
        };
        var fromLogsConnectionOption = new Option<string?>("--from-logs-connection")
        {
            Description =
                "SQLite logs file to read (required for a SQLite source with --include-logs).",
        };
        var toLogsConnectionOption = new Option<string?>("--to-logs-connection")
        {
            Description =
                "SQLite logs file to write (required for a SQLite target with --include-logs).",
        };

        var root = new RootCommand(
            "Copies a MangaIngestWithUpscaling installation between SQLite and PostgreSQL. "
                + "Run it against a stopped application; the target must be empty unless --force is passed."
        );
        root.Add(fromOption);
        root.Add(toOption);
        root.Add(fromConnectionOption);
        root.Add(toConnectionOption);
        root.Add(batchSizeOption);
        root.Add(forceOption);
        root.Add(includeLogsOption);
        root.Add(fromLogsConnectionOption);
        root.Add(toLogsConnectionOption);

        root.SetAction(
            async (parseResult, cancellationToken) =>
            {
                var options = new MigratorOptions(
                    parseResult.GetRequiredValue(fromOption),
                    parseResult.GetRequiredValue(fromConnectionOption),
                    parseResult.GetRequiredValue(toOption),
                    parseResult.GetRequiredValue(toConnectionOption),
                    parseResult.GetValue(batchSizeOption),
                    parseResult.GetValue(forceOption),
                    parseResult.GetValue(includeLogsOption),
                    parseResult.GetValue(fromLogsConnectionOption),
                    parseResult.GetValue(toLogsConnectionOption)
                );

                try
                {
                    await migrateAsync(
                        options,
                        message => parseResult.InvocationConfiguration.Output.WriteLine(message),
                        cancellationToken
                    );
                    return 0;
                }
                catch (Exception ex)
                {
                    // Print the full chain (inner exceptions and stack trace): data migrations fail
                    // inside provider exceptions where the top-level message alone is not actionable.
                    // Redact connection strings first, since provider messages can echo them.
                    parseResult.InvocationConfiguration.Error.WriteLine(
                        Redact(ex.ToString(), options)
                    );
                    return 1;
                }
            }
        );

        return root;
    }

    /// <summary>
    /// Maps a provider name to <see cref="DatabaseProvider"/>, reporting an unrecognized value as a
    /// parse error so the standard help and exit-code handling apply.
    /// </summary>
    private static DatabaseProvider ParseProvider(ArgumentResult result)
    {
        string value = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;
        if (DatabaseProviderResolver.TryResolve(value, out DatabaseProvider provider, out _))
        {
            return provider;
        }

        result.AddError("Unknown provider. Use 'sqlite' or 'postgres'.");
        return default;
    }

    /// <summary>
    /// Removes connection-string secrets from a message before it is written to the console: the
    /// connection strings supplied on the command line, and any <c>Password=</c>/<c>Pwd=</c> value a
    /// provider exception may have embedded.
    /// </summary>
    internal static string Redact(string text, MigratorOptions options)
    {
        foreach (
            string? connection in new[]
            {
                options.FromConnection,
                options.ToConnection,
                options.FromLogsConnection,
                options.ToLogsConnection,
            }
        )
        {
            if (!string.IsNullOrEmpty(connection))
            {
                text = text.Replace(
                    connection,
                    "<connection string redacted>",
                    StringComparison.Ordinal
                );
            }
        }

        // The quoted alternatives allow a doubled delimiter inside the value: connection-string
        // builders escape an embedded quote by doubling it (a password a"b becomes Password="a""b"),
        // so a plain "[^"]*" would stop at the first inner quote and leave the tail visible.
        return Regex.Replace(
            text,
            """(?i)\b(password|pwd)\s*=\s*(?:"(?:[^"]|"")*"|'(?:[^']|'')*'|[^;\r\n]*)""",
            "$1=***"
        );
    }
}
