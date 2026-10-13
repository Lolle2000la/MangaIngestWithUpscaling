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

    /// <summary>
    /// Adds or replaces this device's own fingerprint entry, merging it with what is on disk rather
    /// than replacing the entry wholesale (see <see cref="Merge"/>).
    /// </summary>
    public void Save(string modelsDirectory, DeviceMemoryProfile profile)
    {
        lock (gate)
        {
            string path = PathFor(modelsDirectory);
            try
            {
                // Read the file back before merging rather than using the in-memory snapshot: the
                // snapshot may predate an entry another worker wrote to a shared models volume, and
                // writing it would silently drop that worker's profile. Two processes can still
                // interleave their read and write — the atomic move prevents a torn file, not a lost
                // update — but a stale in-memory snapshot is the failure that was actually hit.
                if (!TryReadFile(path, out Dictionary<string, DeviceMemoryProfile> profiles))
                {
                    // The file exists but could not be read, so its content is unknown rather than
                    // empty. Writing now would build a dictionary from nothing and destroy every
                    // profile in it — exactly the loss the read-back exists to prevent. The caller
                    // already holds the measurement it took, and the next save retries the read once
                    // the file is readable again.
                    logger.LogWarning(
                        "Not persisting the device memory profile to {Path}: the existing file is unreadable",
                        path
                    );
                    return;
                }

                profiles[profile.Fingerprint] = Merge(
                    profiles.GetValueOrDefault(profile.Fingerprint),
                    profile
                );

                // Publish the merged snapshot to the in-memory cache before writing: a save that
                // cannot reach the file must still hand the caller's measurement to the next Load in
                // this process, and the cache is exactly the file's intended content.
                cache = profiles;
                cachePath = path;
                cacheFileSignature = FileSignature(path);

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
                    cacheFileSignature = FileSignature(path);
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
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not persist the device memory profile to {Path}",
                    path
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

        if (!TryReadFile(path, out Dictionary<string, DeviceMemoryProfile> profiles))
        {
            // The file exists but could not be read. Fall back to the last good snapshot for this
            // path if there is one; otherwise hand back an empty dictionary *without* caching it, so
            // "unreadable" is not published as "no profiles" and the next read retries the file.
            return cache is not null && cachePath == path ? cache : profiles;
        }

        cache = profiles;
        cachePath = path;
        cacheFileSignature = FileSignature(path);
        return profiles;
    }

    /// <summary>
    /// Reads the store file at <paramref name="path"/>.
    /// <para>
    /// Returns false only when the file exists but its content could not be read at all (an I/O or
    /// access failure), leaving <paramref name="profiles"/> empty because the real content is
    /// unknown. A missing file and an unparseable one both return true with an empty dictionary:
    /// neither has profiles that could be preserved, so both are safe to re-create.
    /// </para>
    /// </summary>
    private bool TryReadFile(string path, out Dictionary<string, DeviceMemoryProfile> profiles)
    {
        profiles = [];
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            Dictionary<string, DeviceMemoryProfile>? loaded = JsonSerializer.Deserialize<
                Dictionary<string, DeviceMemoryProfile>
            >(stream, Options);
            if (loaded is not null)
            {
                profiles = loaded;
            }

            return true;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Device memory profile file {Path} is corrupt and will be re-created",
                path
            );
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Device memory profile file {Path} is unreadable", path);
            return false;
        }
    }

    /// <summary>
    /// Combines the entry read back from disk with the one being saved, for the same fingerprint.
    /// <para>
    /// A caller measures one key at a time on a profile it loaded earlier, so its object can predate
    /// entries another process added under the same fingerprint since that load. The freshly-read
    /// entry is therefore the base and the caller's per-model entries are layered on top: the model
    /// the caller just measured wins its key, and every other model's measurement survives.
    /// </para>
    /// <para>
    /// The device-wide fields belong to whichever entry actually measured them: a caller that just
    /// ran the device-wide benchmark has <see cref="DeviceMemoryProfile.DeviceWideMeasured"/> set and
    /// wins, while a caller that only measured a model keeps a device-wide measurement another
    /// process wrote after its load rather than replacing it with its own untouched skeleton.
    /// </para>
    /// </summary>
    private static DeviceMemoryProfile Merge(
        DeviceMemoryProfile? existing,
        DeviceMemoryProfile incoming
    )
    {
        if (existing is null)
        {
            return incoming;
        }

        if (existing.Models.Count > 0)
        {
            Dictionary<string, ModelMemoryProfile> models = new(
                existing.Models,
                StringComparer.Ordinal
            );
            foreach (KeyValuePair<string, ModelMemoryProfile> entry in incoming.Models)
            {
                models[entry.Key] = entry.Value;
            }
            incoming.Models = models;
        }

        if (!incoming.DeviceWideMeasured && existing.DeviceWideMeasured)
        {
            incoming.ActivationScale = existing.ActivationScale;
            incoming.SessionReservationBytes = existing.SessionReservationBytes;
            incoming.DeviceWideMeasured = true;
            incoming.CalibratedAtUtc = existing.CalibratedAtUtc;
            incoming.CalibratedWithModel = existing.CalibratedWithModel;
            incoming.MeasuredTiles = existing.MeasuredTiles;
            incoming.MeasuredPeakBytes = existing.MeasuredPeakBytes;
        }

        return incoming;
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
