using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class MangaJaNaiWorkerClientTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(CompressionFormat.Webp, "webp")]
    [InlineData(CompressionFormat.Png, "png")]
    [InlineData(CompressionFormat.Jpg, "jpeg")]
    [InlineData(CompressionFormat.Avif, "avif")]
    public void ToFormatString_MapsCompressionFormatToWorkerFormat(
        CompressionFormat format,
        string expected
    )
    {
        Assert.Equal(expected, MangaJaNaiWorkerClient.ToFormatString(format));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildJobLine_ProducesWellFormedJobRequest()
    {
        var request = new UpscaleJobRequest
        {
            Id = "job-1",
            InputPath = "/data/ch1.cbz",
            OutputFolder = "/out",
            OutputFilename = "chapter-1",
            Format = CompressionFormat.Webp,
            Scale = ScaleFactor.TwoX,
            Overwrite = true,
        };

        string line = MangaJaNaiWorkerClient.BuildJobLine(request);

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;

        Assert.Equal("job", root.GetProperty("type").GetString());
        Assert.Equal("job-1", root.GetProperty("id").GetString());
        Assert.Equal("/data/ch1.cbz", root.GetProperty("input").GetProperty("path").GetString());

        JsonElement output = root.GetProperty("output");
        Assert.Equal("/out", output.GetProperty("folder").GetString());
        Assert.Equal("chapter-1", output.GetProperty("filename").GetString());
        Assert.Equal("webp", output.GetProperty("format").GetString());
        Assert.True(output.GetProperty("overwrite").GetBoolean());

        JsonElement options = root.GetProperty("options");
        Assert.Equal(2, options.GetProperty("scale").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WorkerCommand_SerializesWithSnakeCaseAndOmitsNullId()
    {
        // Pins the source-generated naming policy and null-omission, so the control messages
        // stay AOT-serializable and wire-compatible with worker.py.
        string shutdown = JsonSerializer.Serialize(
            new WorkerCommand("shutdown"),
            WorkerJson.Options
        );
        Assert.Equal("""{"type":"shutdown"}""", shutdown);

        string cancel = JsonSerializer.Serialize(
            new WorkerCommand("cancel", "job-9"),
            WorkerJson.Options
        );
        Assert.Equal("""{"type":"cancel","id":"job-9"}""", cancel);

        string release = JsonSerializer.Serialize(
            new WorkerCommand("release_cache"),
            WorkerJson.Options
        );
        Assert.Equal("""{"type":"release_cache"}""", release);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WorkerEvent_DeserializesCacheReleasedEvent()
    {
        WorkerEvent? evt = JsonSerializer.Deserialize<WorkerEvent>(
            """{"type":"cache_released","status":"busy"}""",
            WorkerJson.Options
        );

        WorkerCacheReleasedEvent released = Assert.IsType<WorkerCacheReleasedEvent>(evt);
        Assert.Equal("busy", released.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildChapterLine_ProducesOpenChapterRequest()
    {
        var request = new ChapterJobRequest
        {
            Id = "chap-1",
            OutputFolder = "/out",
            Format = CompressionFormat.Webp,
            Scale = ScaleFactor.TwoX,
            TotalPages = 42,
        };

        string line = MangaJaNaiWorkerClient.BuildChapterLine(request);

        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.Equal("open_chapter", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("chap-1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(
            "/out",
            doc.RootElement.GetProperty("output").GetProperty("folder").GetString()
        );
        Assert.Equal(
            "webp",
            doc.RootElement.GetProperty("output").GetProperty("format").GetString()
        );
        Assert.Equal(2, doc.RootElement.GetProperty("options").GetProperty("scale").GetInt32());
        Assert.Equal(42, doc.RootElement.GetProperty("total_pages").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildPageLine_ProducesPageRequest()
    {
        string line = MangaJaNaiWorkerClient.BuildPageLine(
            "chap-1",
            new ChapterPage(3, "004.jpg", "/tmp/004.jpg")
        );

        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.Equal("page", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("chap-1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("index").GetInt32());
        Assert.Equal("004.jpg", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("/tmp/004.jpg", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildCloseChapterLine_ProducesCloseChapterRequest()
    {
        string line = MangaJaNaiWorkerClient.BuildCloseChapterLine("chap-1");

        Assert.Equal("""{"type":"close_chapter","id":"chap-1"}""", line);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WorkerEvent_DeserializesPageDoneEvent()
    {
        WorkerEvent? evt = JsonSerializer.Deserialize<WorkerEvent>(
            """{"type":"page_done","id":"chap-1","index":3,"input":"004.jpg","output":"/out/004.webp","status":"upscaled"}""",
            WorkerJson.Options
        );

        WorkerPageDoneEvent pageDone = Assert.IsType<WorkerPageDoneEvent>(evt);
        Assert.Equal("chap-1", pageDone.Id);
        Assert.Equal(3, pageDone.Index);
        Assert.Equal("004.jpg", pageDone.Input);
        Assert.Equal("/out/004.webp", pageDone.Output);
        Assert.Equal("upscaled", pageDone.Status);
    }
}
