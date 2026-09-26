using System;
using MangaIngestWithUpscaling.Components;

namespace MangaIngestWithUpscaling.Tests.UI;

public class LocalTimeTests : BunitContext
{
    public LocalTimeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersUtcInstantAndAsksBrowserToReformat()
    {
        JSInterop.SetupVoid("localTime.formatElement");
        var value = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var cut = Render<LocalTime>(parameters => parameters.Add(p => p.Value, value));

        var span = cut.Find("span");
        Assert.Equal(
            new DateTimeOffset(value).ToUnixTimeMilliseconds().ToString(),
            span.GetAttribute("data-utc-ms")
        );
        Assert.Equal("2024-01-02 03:04:05", span.TextContent);

        cut.WaitForAssertion(() => JSInterop.VerifyInvoke("localTime.formatElement"));
    }

    [Fact]
    public void TreatsUnspecifiedKindAsUtc()
    {
        JSInterop.SetupVoid("localTime.formatElement");
        var value = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        var cut = Render<LocalTime>(parameters => parameters.Add(p => p.Value, value));

        var span = cut.Find("span");
        // The SQLite provider reads timestamps back as Unspecified; the UTC instant must not shift.
        Assert.Equal(
            new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds()
                .ToString(),
            span.GetAttribute("data-utc-ms")
        );
        Assert.Equal("2024-01-02 03:04:05", span.TextContent);
    }
}
