using MangaIngestWithUpscaling.Shared.Services.Analysis;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

/// <summary>
/// The detector's window plan is pure arithmetic, so it is tested directly: what matters is that a page
/// that fits the measured budget is inferred in one pass (windowing re-infers its context rows, so it
/// costs time for nothing when the page fits), that one that does not fit is split into windows whose
/// <em>window</em> — chunk plus the margin on both sides — still fits, and that an unmeasurable budget
/// falls back to the bounded default rather than to "assume the device is roomy".
/// </summary>
public class DetectorWindowPlanTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;
    private const int Margin = 1536;

    [Fact]
    public void PageThatFitsTheBudget_IsOneWholePagePass()
    {
        (int chunkRows, int windowCount) = SplitDetectionService.PlanDetectorWindows(
            pageRows: 7000,
            budgetBytes: 16 * GiB
        );

        Assert.Equal(1, windowCount);
        Assert.Equal(7000, chunkRows);
    }

    [Fact]
    public void PageThatDoesNotFit_IsSplitIntoWindowsThatDo()
    {
        (int chunkRows, int windowCount) = SplitDetectionService.PlanDetectorWindows(
            pageRows: 15000,
            budgetBytes: 8 * GiB
        );

        // Pinned literally, not recomputed from the production constants: these are the documented
        // measurements (DetectorBytesPerRow = 1600000 B/row, DetectorFixedBytes = 32 MiB), so a change
        // to either shows up here instead of quietly re-deriving a passing expectation. 8 GiB × 0.9
        // leaves 7730941132 B for a window; minus 32 MiB that is 4810 affordable *window* rows, and
        // the 1536-row margin on both sides leaves a 1738-row chunk — 9 windows over 15000 rows.
        Assert.Equal(1738, chunkRows);
        Assert.Equal(9, windowCount);

        // The window, not the chunk, is what has to fit: chunk plus the margin on each side (clamped
        // to the page, which the first and last window are).
        long windowRows = Math.Min(15000, chunkRows + (2L * Margin));
        long estimated = (32 * MiB) + (windowRows * 1_600_000);
        Assert.True(
            estimated <= (long)(8 * GiB * 0.9),
            $"a window of {windowRows} rows is estimated at {estimated / MiB} MiB, over the budget"
        );
    }

    [Fact]
    public void PageShorterThanTheMinimumChunk_IsItsOwnSingleWindow()
    {
        // A wide/short page (3000x1800 → 460 resized rows) with a budget too small for the
        // page. Every window's context clamps to the page's own edges, so splitting such a
        // page cannot make its inference smaller than the page — and clamping the chunk up to
        // the minimum inverted Math.Clamp's bounds and threw for exactly these pages, after the
        // model was reached, where the throw was cached as a model verdict for the process.
        // The single pass over the short page is the only plan that exists.
        (int chunkRows, int windowCount) = SplitDetectionService.PlanDetectorWindows(
            pageRows: 460,
            budgetBytes: 64 * MiB
        );

        Assert.Equal(1, windowCount);
        Assert.Equal(460, chunkRows);
    }

    [Fact]
    public void UnmeasurableBudget_UsesTheBoundedDefault()
    {
        // No budget is not evidence of abundance: the default bounds the footprint instead of letting a
        // tall page be inferred whole.
        Assert.Equal((2048, 4), SplitDetectionService.PlanDetectorWindows(7000, 0));
        Assert.Equal((1000, 1), SplitDetectionService.PlanDetectorWindows(1000, 0));

        // A page so large that even the affordable window is below the minimum still gets the minimum
        // chunk rather than hundreds of near-identical windows.
        (int chunkRows, int windowCount) = SplitDetectionService.PlanDetectorWindows(
            7000,
            64 * MiB
        );
        Assert.Equal(512, chunkRows);
        Assert.Equal(14, windowCount);
    }
}
