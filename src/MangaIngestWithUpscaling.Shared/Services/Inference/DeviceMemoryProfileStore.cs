using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// Persists measured <see cref="DeviceMemoryProfile"/>s per device. Several profiles live in one file
/// (keyed by device fingerprint), so a machine that swaps GPUs, or a shared models volume used by
/// several hosts, keeps every calibration it ever made.
/// </summary>
[RegisterSingleton]
public sealed class DeviceMemoryProfileStore
{
    /// <summary>File name written next to the models.</summary>
    public const string FileName = "device-memory-profile.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ILogger<DeviceMemoryProfileStore> logger;
    private readonly Lock gate = new();
    private Dictionary<string, DeviceMemoryProfile>? cache;
    private string? cachePath;

    /// <summary>
    /// Signature of the file the cache was read from. The cache is only served while the file is
    /// unchanged, so an entry another host wrote to a shared models volume is picked up.
    /// </summary>
    private (long Ticks, long Length) cacheFileSignature;

    public DeviceMemoryProfileStore(ILogger<DeviceMemoryProfileStore> logger)
    {
        this.logger = logger;
    }

    /// <summary>Full path of the store file for a models directory.</summary>
    public static string PathFor(string modelsDirectory) =>
        System.IO.Path.Combine(modelsDirectory, FileName);

    /// <summary>
    /// Reads the profile for <paramref name="fingerprint"/>, or null when this device has not been
    /// calibrated (yet). A missing or corrupt file is not an error: the caller falls back to the
    /// built-in coefficients.
    /// </summary>
    public DeviceMemoryProfile? Load(string modelsDirectory, string fingerprint)
    {
        lock (gate)
        {
            // Not forced: the cached snapshot is reused only while the file on disk is untouched, so
            // a profile another worker wrote to a shared models volume is still seen here.
            Dictionary<string, DeviceMemoryProfile> profiles = Read(
                modelsDirectory,
                forceReload: false
            );
            return profiles.TryGetValue(fingerprint, out DeviceMemoryProfile? profile)
                ? profile
                : null;
        }
    }

    /// <summary>Adds or replaces the profile for its own fingerprint.</summary>
    public void Save(string modelsDirectory, DeviceMemoryProfile profile)
    {
        lock (gate)
        {
            try
            {
                // Read the file back before merging rather than using the in-memory snapshot: the
                // snapshot may predate an entry another worker wrote to a shared models volume, and
                // writing it would silently drop that worker's profile. Two processes can still
                // interleave their read and write — the atomic move prevents a torn file, not a lost
                // update — but a stale in-memory snapshot is the failure that was actually hit.
                Dictionary<string, DeviceMemoryProfile> profiles = Read(
                    modelsDirectory,
                    forceReload: true
                );
                profiles[profile.Fingerprint] = profile;
                string path = PathFor(modelsDirectory);
                string? directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // Write to a temp file first so a crash mid-write cannot destroy the other profiles.
                // The name is unique per writer: this file is explicitly shared between hosts, and a
                // fixed ".tmp" name lets two of them interleave the same temp file, so the move below
                // would publish a torn mixture of both rather than one writer's snapshot.
                string temp = TempPath(path);
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(profiles, Options));
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        try
                        {
                            File.Delete(temp);
                        }
                        catch
                        {
                            // Left behind only if the move failed and the delete did too; a stale
                            // uniquely-named temp file is inert, not a correctness problem.
                        }
                    }
                }
                cache = profiles;
                cachePath = path;
                cacheFileSignature = FileSignature(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not persist the device memory profile to {Path}",
                    PathFor(modelsDirectory)
                );
            }
        }
    }

    /// <summary>
    /// The scratch path a save writes to before moving it onto <paramref name="path"/>. Unique per
    /// writer: the file is explicitly shared between hosts, and a fixed <c>.tmp</c> name lets two of
    /// them interleave the same temp file, so the move would publish a torn mixture of both rather
    /// than one writer's snapshot.
    /// </summary>
    internal static string TempPath(string path) =>
        $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    private Dictionary<string, DeviceMemoryProfile> Read(string modelsDirectory, bool forceReload)
    {
        string path = PathFor(modelsDirectory);
        if (
            !forceReload
            && cache is not null
            && cachePath == path
            && FileSignature(path) == cacheFileSignature
        )
        {
            return cache;
        }

        Dictionary<string, DeviceMemoryProfile> profiles = [];
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = File.OpenRead(path);
                Dictionary<string, DeviceMemoryProfile>? loaded = JsonSerializer.Deserialize<
                    Dictionary<string, DeviceMemoryProfile>
                >(stream, Options);
                if (loaded is not null)
                {
                    profiles = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Device memory profile file {Path} is unreadable and will be re-created",
                path
            );

            // Fall back to the last good snapshot instead of treating an unreadable file as empty:
            // otherwise a transient read error would let the next Save overwrite every other
            // profile with only its own entry.
            if (cache is not null && cachePath == path)
            {
                return cache;
            }
        }

        cache = profiles;
        cachePath = path;
        cacheFileSignature = FileSignature(path);
        return profiles;
    }

    /// <summary>
    /// Length and last-write time of the store file, used to notice that another host sharing the
    /// models volume has rewritten it since this instance last read it. The two are taken together
    /// because either alone can miss a rewrite: the timestamp is the reliable signal where the
    /// filesystem records sub-second times, and the length catches most entry changes even where it
    /// does not. A write is never made from this signature — <see cref="Save"/> always re-reads — so
    /// a missed change here only means one extra stale <see cref="Load"/>.
    /// </summary>
    private static (long Ticks, long Length) FileSignature(string path)
    {
        try
        {
            FileInfo info = new(path);
            // A missing file is (0, -1) and unreadable metadata (0, -2), so neither can match the
            // signature of a file that was actually read.
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0L, -1L);
        }
        catch (Exception)
        {
            return (0L, -2L);
        }
    }
}
