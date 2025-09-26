namespace MangaIngestWithUpscaling.Configuration;

public class IntegrityCheckerConfig
{
    public const string Position = "IntegrityChecker";

    /// <summary>
    ///     Max degree of parallelism for integrity checks. If null or <= 0, defaults to Environment.ProcessorCount.
    /// </summary>
    public int? MaxParallelism { get; set; }
}
