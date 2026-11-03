using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Inference;

/// <summary>
/// The model probe verdicts a device keeps between processes.
/// <para>
/// The probe is a 256 px inference, and ONNX Runtime's WebGPU buffer cache never returns its device
/// memory — measured on an RX 9070 XT at ~4.9 GB resident for the rest of the process, which the
/// automatic VRAM budget then has to plan around. A second process has to be able to read the answer
/// instead of paying that cost again.
/// </para>
/// </summary>
public class ModelProbeVerdictTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DeviceMemoryCalibrator _calibrator;

    public ModelProbeVerdictTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"probe_verdicts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Options.Create(
            new UpscalerConfig { ModelsDirectory = _tempDir, UseCPU = true }
        );
        _calibrator = new DeviceMemoryCalibrator(
            new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance),
            config,
            new DeviceMemoryProfileStore(NullLogger<DeviceMemoryProfileStore>.Instance),
            NullLogger<DeviceMemoryCalibrator>.Instance
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private string Model(string name, int bytes = 1024)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void UnknownModel_HasNoVerdict()
    {
        Assert.Null(_calibrator.TryGetModelUsableVerdict(0, _tempDir, Model("a.onnx"), 4));
    }

    [Fact]
    public void RecordedVerdict_IsReadBack()
    {
        string model = Model("a.onnx");

        _calibrator.SetModelUsableVerdict(0, _tempDir, model, 4, usable: true);
        Assert.True(_calibrator.TryGetModelUsableVerdict(0, _tempDir, model, 4));

        // A later answer replaces the earlier one: a driver update can make a model unusable, and the
        // per-tile guard records that through this path.
        _calibrator.SetModelUsableVerdict(0, _tempDir, model, 4, usable: false);
        Assert.False(_calibrator.TryGetModelUsableVerdict(0, _tempDir, model, 4));
    }

    [Fact]
    public void Verdict_IsKeyedByModelAndScale()
    {
        string model = Model("a.onnx");
        _calibrator.SetModelUsableVerdict(0, _tempDir, model, 4, usable: true);

        Assert.Null(_calibrator.TryGetModelUsableVerdict(0, _tempDir, model, 2));
        Assert.Null(_calibrator.TryGetModelUsableVerdict(0, _tempDir, Model("b.onnx"), 4));
    }

    [Fact]
    public void Verdict_IsKeyedByFileSize()
    {
        // A re-exported file can keep its name while its graph changes, so the size is part of the key
        // (the same rule the device memory profile uses).
        string model = Model("a.onnx");
        _calibrator.SetModelUsableVerdict(0, _tempDir, model, 4, usable: true);

        File.WriteAllBytes(model, new byte[2048]);
        Assert.Null(_calibrator.TryGetModelUsableVerdict(0, _tempDir, model, 4));
    }

    [Fact]
    public void CorruptFile_ReadsAsUnknownAndCanBeRewritten()
    {
        File.WriteAllText(DeviceMemoryCalibrator.VerdictPath(_tempDir), "{ this is not json");

        Assert.Null(_calibrator.TryGetModelUsableVerdict(0, _tempDir, Model("a.onnx"), 4));
        _calibrator.SetModelUsableVerdict(0, _tempDir, Model("a.onnx"), 4, usable: true);
        Assert.True(_calibrator.TryGetModelUsableVerdict(0, _tempDir, Model("a.onnx"), 4));
    }

    [Fact]
    public void UnwritableDirectory_IsSwallowed()
    {
        // The verdict is an optimisation. A models directory that cannot be written (a read-only
        // volume, a container running as an arbitrary uid) must not fail the page.
        const string readOnly = "/proc/definitely-not-writable";

        Assert.Null(_calibrator.TryGetModelUsableVerdict(0, readOnly, Model("a.onnx"), 4));
        _calibrator.SetModelUsableVerdict(0, readOnly, Model("a.onnx"), 4, usable: true);
    }

    [Fact]
    public void VerdictsOfSeveralModelsAndScales_Coexist()
    {
        string first = Model("a.onnx");
        string second = Model("b.onnx");

        _calibrator.SetModelUsableVerdict(0, _tempDir, first, 4, usable: true);
        _calibrator.SetModelUsableVerdict(0, _tempDir, first, 2, usable: false);
        _calibrator.SetModelUsableVerdict(0, _tempDir, second, 4, usable: false);

        Assert.True(_calibrator.TryGetModelUsableVerdict(0, _tempDir, first, 4));
        Assert.False(_calibrator.TryGetModelUsableVerdict(0, _tempDir, first, 2));
        Assert.False(_calibrator.TryGetModelUsableVerdict(0, _tempDir, second, 4));
    }
}
