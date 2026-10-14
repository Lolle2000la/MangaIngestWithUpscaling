using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AutoRegisterInject;
using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MangaIngestWithUpscaling.Shared.Services.Inference;

/// <summary>
/// Runs the calibration benchmark in a child process.
/// <para>
/// The benchmark's two sessions leave device memory the execution provider never returns — measured on
/// an RX 9070 XT at 3012 MB for the fp16 ESRGAN (§2.2 of <c>docs/UPSCALING_MEMORY_MODEL.md</c>) — and
/// the only thing that gives it back is the process exiting. Run in the host itself, that memory is
/// resident while the first page runs, and the page plan (which is sized from the budget minus the
/// session reservation, not minus this) then spends the safety margin on it: measured at 15636 MB of a
/// 16304 MB card for a cold first page against 12668 MB once the profile exists.
/// </para>
/// <para>
/// A failed child is not a failed calibration: the caller falls back to measuring in its own process,
/// which is what happened before this existed.
/// </para>
/// </summary>
public interface IDeviceMemoryBenchmarkProcessRunner
{
    /// <summary>
    /// Runs the benchmark for <paramref name="modelPath"/> in a child process and reports whether it
    /// produced a usable measurement. Never throws for a child that could not be started or failed.
    /// </summary>
    Task<bool> RunAsync(
        UpscalerConfig config,
        string modelPath,
        int scale,
        int deviceId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The runner used where spawning a child is wrong: inside the child itself (which would recurse), and
/// as the default for a caller that does not supply one.
/// </summary>
public sealed class NoDeviceMemoryBenchmarkProcessRunner : IDeviceMemoryBenchmarkProcessRunner
{
    public static readonly NoDeviceMemoryBenchmarkProcessRunner Instance = new();

    public Task<bool> RunAsync(
        UpscalerConfig config,
        string modelPath,
        int scale,
        int deviceId,
        CancellationToken cancellationToken
    ) => Task.FromResult(false);
}

/// <summary>
/// Spawns the host's own executable in <see cref="DeviceMemoryBenchmarkHost"/> mode.
/// </summary>
[RegisterSingleton]
public sealed class DeviceMemoryBenchmarkProcessRunner(
    ILogger<DeviceMemoryBenchmarkProcessRunner> logger
) : IDeviceMemoryBenchmarkProcessRunner
{
    public async Task<bool> RunAsync(
        UpscalerConfig config,
        string modelPath,
        int scale,
        int deviceId,
        CancellationToken cancellationToken
    )
    {
        // The host's own executable: the child needs exactly the deployment the parent runs, including
        // its native ONNX Runtime and its execution provider, and this is the one binary guaranteed to
        // be there.
        string? host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        (string fileName, string[] prefix) = ChildCommand(
            host,
            Assembly.GetEntryAssembly()?.Location
        );

        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in prefix)
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(DeviceMemoryBenchmarkHost.CommandName);
            start.ArgumentList.Add(DeviceMemoryBenchmarkHost.EncodeConfig(config));
            start.ArgumentList.Add(modelPath);
            start.ArgumentList.Add(scale.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(deviceId.ToString(CultureInfo.InvariantCulture));

            using Process? child = Process.Start(start);
            if (child is null)
            {
                return false;
            }

            // A child that hangs must not hang the page behind it. The benchmark takes seconds; this is
            // a net for a child that never reaches its own exit — a mixed deployment whose binary has
            // no benchmark mode and would start a server instead, a wedged driver call. On expiry the
            // child is killed and the caller measures in process, exactly as it does when the spawn
            // itself fails. (Not unit-tested: it needs a real child process to hang.)
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ChildTimeout);

            try
            {
                // Both pipes have to be drained while the child runs: a redirected pipe nobody reads
                // fills and blocks the child, and the benchmark would then hang instead of failing.
                Task<string> stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> stderr = child.StandardError.ReadToEndAsync(timeout.Token);
                await child.WaitForExitAsync(timeout.Token);
                await stdout;
                string error = await stderr;

                if (child.ExitCode == 0)
                {
                    return true;
                }

                logger.LogWarning(
                    "The device memory benchmark child process exited with {ExitCode}: {Error}",
                    child.ExitCode,
                    error.Trim()
                );
                return false;
            }
            finally
            {
                // Covers both the timeout and a cancelled page: neither should leave a benchmark
                // running behind the host. A child that already exited is left alone.
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not run the device memory benchmark in a child process; measuring in this process instead"
            );
            return false;
        }
    }

    /// <summary>
    /// How long a child may take before it is killed and the caller measures in process.
    /// </summary>
    internal static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The command that starts the child: the host's own executable, plus the entry assembly when that
    /// executable is the <c>dotnet</c> muxer rather than an apphost.
    /// <para>
    /// The production container starts the app as <c>dotnet MangaIngestWithUpscaling.dll</c>
    /// (<c>Dockerfile</c>), and so does any framework-dependent invocation: there
    /// <c>Environment.ProcessPath</c> is the muxer, which does not know the benchmark option and would
    /// either refuse it or try to run a file by that name. Handing it the assembly first makes the
    /// child the same application either way. A single-file publish has no separate assembly, and
    /// there the process path is the app itself.
    /// </para>
    /// </summary>
    internal static (string FileName, string[] Prefix) ChildCommand(
        string processPath,
        string? entryAssemblyPath
    )
    {
        bool muxer = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase
        );
        return muxer && !string.IsNullOrEmpty(entryAssemblyPath)
            ? (processPath, [entryAssemblyPath])
            : (processPath, []);
    }
}

/// <summary>
/// The hidden command both hosts expose so <see cref="DeviceMemoryBenchmarkProcessRunner"/> can spawn
/// them. Each host calls <see cref="RunAsync"/> before it builds its own host, so the child pays
/// neither the web server nor the database.
/// </summary>
public static class DeviceMemoryBenchmarkHost
{
    /// <summary>The first argument that selects this mode.</summary>
    public const string CommandName = "--device-memory-benchmark";

    /// <summary>Whether <paramref name="args"/> asks for this mode.</summary>
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.Ordinal);

    /// <summary>
    /// The configuration as one argument. Passed rather than re-read from the environment so the child
    /// measures the configuration the parent actually resolved — including the backend, the device
    /// index and the models directory — instead of whatever the child's own working directory and
    /// environment happen to produce.
    /// </summary>
    internal static string EncodeConfig(UpscalerConfig config) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, ConfigJson))
        );

    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = false };

    /// <summary>
    /// Measures one model and exits. Exit code 0 means the profile now carries the measurement; 1 means
    /// the benchmark produced nothing usable, 2 a malformed command line and 3 a thrown failure.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                $"usage: {CommandName} <base64 config> <model path> <scale> <device id>"
            );
            Console.Error.Flush();
            return 2;
        }

        try
        {
            UpscalerConfig config =
                JsonSerializer.Deserialize<UpscalerConfig>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(args[1])),
                    ConfigJson
                )
                ?? throw new InvalidOperationException("the configuration argument did not decode");

            string modelPath = args[2];
            int scale = int.Parse(args[3], CultureInfo.InvariantCulture);
            int deviceId = int.Parse(args[4], CultureInfo.InvariantCulture);

            // This process exists to measure, so the stored profile must not short-circuit it.
            config.RecalibrateDeviceMemory = true;

            var options = Options.Create(config);
            var store = new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance);
            var factory = new OnnxSessionFactory(options, NullLogger<OnnxSessionFactory>.Instance);
            var calibrator = new DeviceMemoryCalibrator(
                factory,
                options,
                store,
                NullLogger<DeviceMemoryCalibrator>.Instance,
                // The child never spawns a child: it would recurse, and its own memory is already the
                // throwaway one this mode exists for.
                NoDeviceMemoryBenchmarkProcessRunner.Instance
            );

            DeviceMemoryProfile? profile = await calibrator.GetOrCalibrateForModelAsync(
                deviceId,
                config.ModelsDirectory,
                modelPath,
                scale,
                CancellationToken.None
            );
            if (profile is null)
            {
                return 1;
            }

            return profile.Models.ContainsKey(
                DeviceMemoryProfile.ModelKey(modelPath, new FileInfo(modelPath).Length)
            )
                ? 0
                : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.Flush();
            return 3;
        }
    }
}
