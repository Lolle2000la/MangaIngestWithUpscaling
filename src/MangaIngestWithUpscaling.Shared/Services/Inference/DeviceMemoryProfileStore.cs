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
            Dictionary<string, DeviceMemoryProfile> profiles = Read(modelsDirectory);
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
                Dictionary<string, DeviceMemoryProfile> profiles = Read(modelsDirectory);
                profiles[profile.Fingerprint] = profile;
                string path = PathFor(modelsDirectory);
                string? directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // Write to a temp file first so a crash mid-write cannot destroy the other profiles
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(profiles, Options));
                File.Move(temp, path, overwrite: true);
                cache = profiles;
                cachePath = path;
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

    private Dictionary<string, DeviceMemoryProfile> Read(string modelsDirectory)
    {
        string path = PathFor(modelsDirectory);
        if (cache is not null && cachePath == path)
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
        }

        cache = profiles;
        cachePath = path;
        return profiles;
    }
}
