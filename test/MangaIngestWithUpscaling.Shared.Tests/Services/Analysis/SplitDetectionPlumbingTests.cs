using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using MangaIngestWithUpscaling.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetVips;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

/// <summary>
/// The non-error half of split detection: the image is decoded, resized to the detector's fixed
/// width, flattened and packed as one plane per channel; the peak mask is read per row; a margin is
/// dropped from the top and bottom; and each remaining row is mapped back to the coordinate in the
/// original page. These were only covered by an end-to-end test against a real detector and a real
/// page, so nothing ran on a machine without the 3.3 GB of models the test looked for.
/// <para>
/// The detector model is generated, and it maps a row's mean channel value to a peak when it exceeds
/// 0.5. That is enough to make the row content of the test image the thing under test: a band of
/// bright rows produces a band of splits, and where that band lands in the original image is decided
/// by the resize and the coordinate mapping rather than by any model behaviour.
/// </para>
/// </summary>
public class SplitDetectionPlumbingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("split_plumbing").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    // 384 x 1000 resizes to the detector's 768-wide input by exactly 2, which keeps the row
    // arithmetic exact: input row k becomes output row 2k, and the alternating output rows are the
    // average of their two neighbours.
    private const int SourceWidth = 384;

    private const int SourceHeight = 1000;
    private const int DetectorWidth = 768;
    private const int ResizedHeight = SourceHeight * 2;
    private const int EdgeMargin = 100;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectSplitsAsync_MapsSplitsBackToTheOriginalsCoordinates()
    {
        // A bright band in the middle of the page, and nothing else bright. Its edges in the original
        // image are 500 and 600; a resized row y is a split when its mapped original coordinate is
        // inside that band. If the mapping were the identity — or if it used the wrong scale — the
        // reported positions would sit hundreds of rows away from where the band is.
        Task<SplitDetectionResult> result = Run(WriteImage("band.png", brightRows: [500, 600]));

        var detection = (await result).Splits;
        Assert.NotEmpty(detection);

        Assert.All(detection, split => Assert.InRange(split.YOriginal, 499, 600));
        // A contiguous run of about a hundred rows, which is the band's width. The exact edges are
        // libvips' business — a linear kernel straddles the band boundary by a row — so the span is
        // asserted rather than the two edge rows, and the band's position is what is being tested.
        int[] positions = detection.Select(s => s.YOriginal).Distinct().OrderBy(y => y).ToArray();
        Assert.InRange(positions.Length, 95, 105);
        Assert.Equal(positions[^1] - positions[0] + 1, positions.Length);

        // The rows straddling the band's edge are half white, and a row mean of just over 0.5 is
        // still a split — so the two ends carry a partial confidence while the interior does not.
        Assert.All(detection, split => Assert.InRange(split.Confidence, 0.5, 1.0));
        Assert.True(detection.Count(s => s.Confidence > 0.9) > positions.Length / 2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectSplitsAsync_IgnoresRowsInsideTheEdgeMargin()
    {
        // Two bands, both inside the margin: one at the very top and one at the very bottom. The
        // margin is 100 rows of the resized image — 50 rows of the original at 2x — so both bands are
        // entirely inside it and must produce nothing at all.
        Task<SplitDetectionResult> bright = Run(
            WriteImage("edges.png", brightRows: [0, 40, 960, 1000])
        );

        Assert.Empty((await bright).Splits);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectSplitsAsync_ReadsOnePlanePerChannel()
    {
        // The tensor layout. A page that is only red has a row mean of 0.2 and must not trigger; the
        // same value in all three planes has a row mean of 0.6 and must. Detector confidence is 1.0
        // here, which distinguishes the two cases through the model rather than through the page.
        SplitDetectionResult redOnly = await Run(
            WriteImage("red.png", brightRows: [500, 600], onlyRed: true)
        );
        Assert.Empty(redOnly.Splits);

        SplitDetectionResult full = await Run(WriteImage("all.png", brightRows: [500, 600]));
        Assert.InRange(full.Splits.Select(s => s.YOriginal).Distinct().Count(), 95, 105);
        Assert.All(full.Splits, split => Assert.InRange(split.Confidence, 0.5, 1.0));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectSplitsAsync_ReportsTheOriginalsDimensionsAndASingleResult()
    {
        SplitDetectionResult result = await Run(WriteImage("dims.png", brightRows: [500, 600]));

        Assert.Null(result.Error);
        Assert.Equal(SourceWidth, result.OriginalWidth);
        Assert.Equal(SourceHeight, result.OriginalHeight);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectSplitsAsync_WithoutAnyBrightRow_FindsNothing()
    {
        // A dark page. This is the case a grayscale manga page is in: no colour anywhere, so no
        // split is proposed.
        SplitDetectionResult result = await Run(WriteImage("dark.png", brightRows: []));

        Assert.Empty(result.Splits);
        Assert.Equal(0, result.Count);
    }

    private async Task<SplitDetectionResult> Run(string imagePath)
    {
        // Written under the name ResolveModelPath looks for, and into the models directory it
        // looks in first. A different name would fall through to whatever model happens to be
        // somewhere else on the machine, and the test would pass for a reason nobody chose.
        TinyOnnxModel.WritePeakMaskDetector(_dir, fileName: "page_break_detector.onnx");
        var config = Options.Create(
            new UpscalerConfig
            {
                // ResolveModelPath looks here first, so the process-wide SplitDetectionLayout.Root
                // does not have to be touched for this image to find its model.
                UseCPU = true,
                ModelsDirectory = _dir,
            }
        );
        using var factory = new OnnxSessionFactory(config, NullLogger<OnnxSessionFactory>.Instance);
        var service = new SplitDetectionService(
            factory,
            NullLogger<SplitDetectionService>.Instance,
            Substitute.For<IStringLocalizer<SplitDetectionService>>(),
            config
        );

        List<SplitDetectionResult> results = await service.DetectSplitsAsync(
            imagePath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Single(results);
        return results[0];
    }

    /// <summary>
    /// Writes a page whose rows in <paramref name="brightRows"/> are white and the rest black.
    /// <paramref name="onlyRed"/> puts the brightness in the red plane alone, so the row mean is a
    /// third of the value and the layout is what decides whether it triggers.
    /// </summary>
    private string WriteImage(string fileName, int[] brightRows, bool onlyRed = false)
    {
        // (start, end) pairs, so a single band is one entry and two bands are four rows.
        var bands = new List<(int From, int To)>();
        for (int i = 0; i + 1 < brightRows.Length; i += 2)
        {
            bands.Add((brightRows[i], brightRows[i + 1]));
        }

        bool IsBright(int y) => bands.Any(band => y >= band.From && y < band.To);

        var pixels = new byte[SourceWidth * SourceHeight * 3];
        for (int y = 0; y < SourceHeight; y++)
        {
            byte value = IsBright(y) ? (byte)255 : (byte)0;
            for (int x = 0; x < SourceWidth; x++)
            {
                int offset = (y * SourceWidth + x) * 3;
                pixels[offset] = value;
                pixels[offset + 1] = onlyRed ? (byte)0 : value;
                pixels[offset + 2] = onlyRed ? (byte)0 : value;
            }
        }

        string path = Path.Combine(_dir, fileName);
        using (
            var image = Image.NewFromMemory(
                pixels,
                SourceWidth,
                SourceHeight,
                3,
                Enums.BandFormat.Uchar
            )
        )
        {
            image.WriteToFile(path);
        }

        return path;
    }
}
