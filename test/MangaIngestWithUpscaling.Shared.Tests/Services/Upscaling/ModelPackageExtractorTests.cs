using System.IO.Compression;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class ModelPackageExtractorTests : IDisposable
{
    private readonly string _dir;

    public ModelPackageExtractorTests() =>
        _dir = Path.Combine(Path.GetTempPath(), $"pkg_extract_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Extract_WithNoSuffix_KeepsTheArchivedNames()
    {
        CreateArchive(
            "pkg.zip",
            ("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx", "fp16 content"),
            ("4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx", "other content")
        );

        ModelPackageExtractor.Extract(Path.Combine(_dir, "pkg.zip"), _dir, null);

        Assert.Equal(
            "fp16 content",
            File.ReadAllText(
                Path.Combine(_dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx")
            )
        );
        Assert.Equal(
            "other content",
            File.ReadAllText(Path.Combine(_dir, "4x_MangaJaNai_1600p_V1_ESRGAN_70k.onnx"))
        );
    }

    [Fact]
    public void Extract_WithTheFp32Suffix_RenamesSoBothPrecisionsCoexist()
    {
        // Both archives carry the same file names; the fp32 side has to come out under a
        // different one, or installing it would delete the fp16 file it is meant to fall back to.
        CreateArchive(
            "pkg.zip",
            ("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx", "fp32 content"),
            ("4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16.onnx", "hat fp32 content")
        );

        ModelPackageExtractor.Extract(
            Path.Combine(_dir, "pkg.zip"),
            _dir,
            ModelFileNames.Fp32Suffix
        );

        Assert.Equal(
            "fp32 content",
            File.ReadAllText(
                Path.Combine(_dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx")
            )
        );
        Assert.Equal(
            "hat fp32 content",
            File.ReadAllText(
                Path.Combine(_dir, "4x_IllustrationJaNai_V3detail_HAT_L_28k_fp32.onnx")
            )
        );
    }

    [Fact]
    public void Extract_TwoPrecisionsOfTheSameArchive_DoNotOverwriteEachOther()
    {
        string zip = Path.Combine(_dir, "pkg.zip");
        CreateArchive(
            "pkg.zip",
            ("4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx", "fp32 content")
        );

        ModelPackageExtractor.Extract(zip, _dir, ModelFileNames.Fp32Suffix);
        File.WriteAllText(
            Path.Combine(_dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx"),
            "fp16 content"
        );
        ModelPackageExtractor.Extract(zip, _dir, ModelFileNames.Fp32Suffix);

        Assert.Equal(
            "fp16 content",
            File.ReadAllText(
                Path.Combine(_dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16.onnx")
            )
        );
        Assert.Equal(
            "fp32 content",
            File.ReadAllText(
                Path.Combine(_dir, "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx")
            )
        );
    }

    [Fact]
    public void Extract_LeavesNoScratchDirectoryBehind()
    {
        CreateArchive("pkg.zip", ("model.onnx", "content"));

        ModelPackageExtractor.Extract(
            Path.Combine(_dir, "pkg.zip"),
            _dir,
            ModelFileNames.Fp32Suffix
        );

        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "model_extract_*"));
    }

    [Fact]
    public void Extract_CreatesTheModelsDirectoryWhenItIsMissing()
    {
        string nested = Path.Combine(_dir, "does", "not", "exist");
        CreateArchive("pkg.zip", ("model.onnx", "content"));

        ModelPackageExtractor.Extract(
            Path.Combine(_dir, "pkg.zip"),
            nested,
            ModelFileNames.Fp32Suffix
        );

        Assert.Equal("content", File.ReadAllText(Path.Combine(nested, "model_fp32.onnx")));
    }

    private void CreateArchive(string name, params (string FileName, string Content)[] entries)
    {
        Directory.CreateDirectory(_dir);
        using var zip = ZipFile.Open(Path.Combine(_dir, name), ZipArchiveMode.Create);
        foreach (var (fileName, content) in entries)
        {
            using var entry = zip.CreateEntry(fileName).Open();
            using var writer = new StreamWriter(entry);
            writer.Write(content);
        }
    }
}
