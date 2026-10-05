using System.Text.Json;
using MangaIngestWithUpscaling.Shared.Services.Analysis;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Analysis;

public class DetectServerProtocolTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void DetectRequest_SerializesWithSnakeCaseKeys()
    {
        string line = JsonSerializer.Serialize(
            new DetectServerRequest { Id = "p1", Path = "/pages/001.jpg" },
            DetectServerJsonContext.Default.DetectServerRequest
        );

        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.Equal("detect", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("/pages/001.jpg", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_OmitsNullId()
    {
        string releaseLine = JsonSerializer.Serialize(
            new DetectServerCommand("release_cache"),
            DetectServerJsonContext.Default.DetectServerCommand
        );
        using JsonDocument releaseDoc = JsonDocument.Parse(releaseLine);
        Assert.Equal("release_cache", releaseDoc.RootElement.GetProperty("type").GetString());
        Assert.False(releaseDoc.RootElement.TryGetProperty("id", out _));

        string cancelLine = JsonSerializer.Serialize(
            new DetectServerCommand("cancel", "p1"),
            DetectServerJsonContext.Default.DetectServerCommand
        );
        using JsonDocument cancelDoc = JsonDocument.Parse(cancelLine);
        Assert.Equal("cancel", cancelDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p1", cancelDoc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadyEvent_Deserializes()
    {
        var evt = JsonSerializer.Deserialize(
            """{"type":"ready","device":"cuda","target_width":768}""",
            DetectServerJsonContext.Default.DetectServerEvent
        );
        var ready = Assert.IsType<DetectServerReadyEvent>(evt);
        Assert.Equal("cuda", ready.Device);
        Assert.Equal(768, ready.TargetWidth);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResultEvent_DeserializesNestedDetectionResult()
    {
        var evt = JsonSerializer.Deserialize(
            """
            {"type":"result","id":"p1","result":{"image":"/pages/001.jpg","original_height":1000,"original_width":768,"splits":[{"y_original":400,"confidence":0.93}],"count":1}}
            """,
            DetectServerJsonContext.Default.DetectServerEvent
        );

        var result = Assert.IsType<DetectServerResultEvent>(evt);
        Assert.Equal("p1", result.Id);
        Assert.NotNull(result.Result);
        Assert.Equal("/pages/001.jpg", result.Result!.ImagePath);
        Assert.Equal(1000, result.Result.OriginalHeight);
        Assert.Single(result.Result.Splits);
        Assert.Equal(400, result.Result.Splits[0].YOriginal);
        Assert.Equal(0.93, result.Result.Splits[0].Confidence);
        Assert.Equal(1, result.Result.Count);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"type":"error","id":"p1","message":"boom"}""", typeof(DetectServerErrorEvent))]
    [InlineData("""{"type":"cancelled","id":"p1"}""", typeof(DetectServerCancelledEvent))]
    [InlineData(
        """{"type":"cache_released","status":"ok"}""",
        typeof(DetectServerCacheReleasedEvent)
    )]
    [InlineData("""{"type":"pong"}""", typeof(DetectServerPongEvent))]
    [InlineData("""{"type":"exited"}""", typeof(DetectServerExitedEvent))]
    public void ControlEvents_Deserialize(string line, Type expected)
    {
        var evt = JsonSerializer.Deserialize(
            line,
            DetectServerJsonContext.Default.DetectServerEvent
        );
        Assert.IsType(expected, evt);
    }
}
