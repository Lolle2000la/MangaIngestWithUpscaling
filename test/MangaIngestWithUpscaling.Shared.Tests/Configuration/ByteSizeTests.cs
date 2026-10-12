using MangaIngestWithUpscaling.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Configuration;

public class ByteSizeTests
{
    [Theory]
    [InlineData("1GiB", 1073741824L)]
    [InlineData("1 GiB", 1073741824L)]
    [InlineData("1gib", 1073741824L)]
    [InlineData("1024MiB", 1073741824L)]
    [InlineData("512 MiB", 536870912L)]
    [InlineData("350MiB", 367001600L)]
    [InlineData("2 KiB", 2048L)]
    [InlineData("8B", 8L)]
    [InlineData("0", 0L)]
    [InlineData("1073741824", 1073741824L)] // plain byte count: existing configuration files
    [InlineData("1.5GiB", 1610612736L)]
    [InlineData("1.5 GiB", 1610612736L)]
    public void Parse_AcceptsBinaryDecimalAndBareForms(string text, long expected)
    {
        Assert.Equal(expected, ByteSize.Parse(text).Bytes);
        Assert.True(ByteSize.TryParse(text, out ByteSize result));
        Assert.Equal(expected, result.Bytes);
    }

    [Theory]
    [InlineData("1GB", 1000000000L)]
    [InlineData("1 GB", 1000000000L)]
    [InlineData("500KB", 500000L)]
    [InlineData("2MB", 2000000L)]
    public void Parse_DecimalSuffixesArePowersOf1000(string text, long expected) =>
        Assert.Equal(expected, ByteSize.Parse(text).Bytes);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GiB")] // unit without a number
    [InlineData("1Gi")] // not a suffix
    [InlineData("1GiBx")]
    [InlineData("1 GiB extra")]
    [InlineData("-1GiB")] // negatives are not a valid size
    [InlineData("1e3GiB")] // exponent notation is deliberately unsupported
    [InlineData("abc")]
    public void Parse_RejectsNonsense(string text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ByteSize.Parse(text));
    }

    [Fact]
    public void Parse_RejectsOutOfRangeValues()
    {
        Assert.False(ByteSize.TryParse("99999 EiB", out _));
        Assert.False(ByteSize.TryParse("99999999999999999999", out _));

        // Representable as a decimal on its own, but not once multiplied by EiB: this is the
        // multiplication's own overflow guard, which the two cases above never reach (the first is
        // rejected by the long range check, the second by the decimal parse). Without the guard the
        // overflow escapes TryParse as an exception instead of "not a size".
        Assert.False(ByteSize.TryParse("10000000000000000000000000000 EiB", out _));
        Assert.Throws<FormatException>(() => ByteSize.Parse("10000000000000000000000000000 EiB"));
    }

    [Fact]
    public void Parse_RejectsAValueOneAboveLongMaxValue()
    {
        // 2^63: the old double bound compared equal to it (long.MaxValue converts to 2^63), so it
        // slipped through and the cast wrapped it to a negative value. It must be rejected like any
        // other value that does not fit a long.
        Assert.False(ByteSize.TryParse("9223372036854775808", out _));
        Assert.Throws<FormatException>(() => ByteSize.Parse("9223372036854775808"));
    }

    [Fact]
    public void Parse_AcceptsLongMaxValueExactly()
    {
        Assert.True(ByteSize.TryParse("9223372036854775807", out ByteSize result));
        Assert.Equal(long.MaxValue, result.Bytes);
        Assert.Equal(long.MaxValue, ByteSize.Parse("9223372036854775807").Bytes);
    }

    [Fact]
    public void Parse_Null_IsNotASize()
    {
        Assert.False(ByteSize.TryParse(null, out _));
        Assert.Throws<FormatException>(() => ByteSize.Parse(null!));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(8L, "8 B")]
    [InlineData(1024L, "1 KiB")]
    [InlineData(367001600L, "350 MiB")]
    [InlineData(1073741824L, "1 GiB")]
    [InlineData(1610612736L, "1.5 GiB")]
    [InlineData(8589934592L, "8 GiB")]
    [InlineData(1500000000L, "1.4 GiB")] // binary-first: the arithmetic is binary
    public void ToString_FormatsWithTheLargestUnit(long bytes, string expected) =>
        Assert.Equal(expected, new ByteSize(bytes).ToString());

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        foreach (
            long bytes in new long[] { 0, 512, 367001600, 1073741824, 1610612736, 21474836480 }
        )
        {
            string text = new ByteSize(bytes).ToString();
            Assert.Equal(bytes, ByteSize.Parse(text).Bytes);
        }
    }

    [Fact]
    public void ToString_IsHumanReadableButNotLosslessAtAUnitBoundary()
    {
        // One byte below 1 MiB. The largest unit that keeps the value at least 1 is the decimal MB
        // (1048575 / 1e6 = 1.048575), so the "0.##" format rounds that quotient to "1.05 MB" — a
        // readable rendering that no longer parses back to the exact byte count. This is a deliberate
        // trade-off, not a Parse bug: a lossless encoding would have to give up the whole-number
        // rendering the logs expect (1500000000 -> "1.4 GiB"). Pinned so a future format change has to
        // decide consciously.
        Assert.Equal("1.05 MB", new ByteSize(1048575).ToString());

        // A binary-aligned value still renders in the binary unit, which is what the logs mostly show.
        Assert.Equal("1 MiB", new ByteSize(1048576).ToString());
        Assert.Equal(1048576L, ByteSize.Parse("1 MiB").Bytes);
    }

    [Fact]
    public void ImplicitConversions_StayExact()
    {
        const long Bytes = 1073741824L;
        ByteSize size = Bytes;
        long round = size;
        Assert.Equal(Bytes, round);
        Assert.Equal(Bytes, size.Bytes);
    }

    [Fact]
    public void UpscalerConfig_DefaultsStayZero()
    {
        // Unset config values must mean "auto-detect", never a budget of zero bytes by accident
        var config = new UpscalerConfig();
        Assert.Equal(0, config.ResolvedMemoryBudgetBytes);
        Assert.Null(config.ResolvedVramSafetyMarginBytes);
        Assert.Null(config.ResolvedVramExclusiveThresholdBytes);
        Assert.Equal(8L * 1024 * 1024 * 1024, config.ResolvedMaxSpoolBytesPerTask);
    }

    [Theory]
    [InlineData("1GiB", 1073741824L)]
    [InlineData("12288MiB", 12884901888L)]
    [InlineData("3221225472", 3221225472L)]
    [InlineData("garbage", 0L)] // an unparseable value must not crash, it falls back
    [InlineData(null, 0L)]
    public void UpscalerConfig_ParsesMemoryBudget(string? configured, long expected)
    {
        var config = new UpscalerConfig { MemoryBudgetBytes = configured };
        Assert.Equal(expected, config.ResolvedMemoryBudgetBytes);
    }

    [Fact]
    public void UpscalerConfig_ParsesOptionalSizes()
    {
        var config = new UpscalerConfig
        {
            VramSafetyMarginBytes = "512MiB",
            VramExclusiveThresholdBytes = "1 GiB",
            MaxSpoolBytesPerTask = "4GiB",
        };

        Assert.Equal(536870912L, config.ResolvedVramSafetyMarginBytes);
        Assert.Equal(1073741824L, config.ResolvedVramExclusiveThresholdBytes);
        Assert.Equal(4294967296L, config.ResolvedMaxSpoolBytesPerTask);
    }

    [Fact]
    public void UpscalerConfig_BindsSizeStringsFromConfiguration()
    {
        // End-to-end through the same configuration system the app uses: environment variables and
        // appsettings both deliver strings.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{UpscalerConfig.Position}:MemoryBudgetBytes"] = "4GiB",
                    [$"{UpscalerConfig.Position}:VramSafetyMarginBytes"] = "512MiB",
                    [$"{UpscalerConfig.Position}:VramExclusiveThresholdBytes"] = "1GiB",
                    [$"{UpscalerConfig.Position}:MaxSpoolBytesPerTask"] = "2GiB",
                }
            )
            .Build();
        UpscalerConfig config = configuration
            .GetSection(UpscalerConfig.Position)
            .Get<UpscalerConfig>()!;

        Assert.Equal(4294967296L, config.ResolvedMemoryBudgetBytes);
        Assert.Equal(536870912L, config.ResolvedVramSafetyMarginBytes);
        Assert.Equal(1073741824L, config.ResolvedVramExclusiveThresholdBytes);
        Assert.Equal(2147483648L, config.ResolvedMaxSpoolBytesPerTask);
    }

    [Fact]
    public void UpscalerConfig_BindsBareByteCountsFromConfiguration()
    {
        // Backwards compatibility: existing configuration files use plain numbers.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{UpscalerConfig.Position}:MemoryBudgetBytes"] = "3221225472",
                    [$"{UpscalerConfig.Position}:MaxSpoolBytesPerTask"] = "100",
                }
            )
            .Build();
        UpscalerConfig config = configuration
            .GetSection(UpscalerConfig.Position)
            .Get<UpscalerConfig>()!;

        Assert.Equal(3221225472L, config.ResolvedMemoryBudgetBytes);
        Assert.Equal(100L, config.ResolvedMaxSpoolBytesPerTask);
    }

    [Theory]
    [InlineData(1_000_000_000L, "1 GB")] // the decimal family is reachable, not shadowed by the binary one
    [InlineData(1_073_741_824L, "1 GiB")]
    [InlineData(1_050_000_000L, "1.05 GB")]
    [InlineData(1_100_000_000L, "1.02 GiB")]
    [InlineData(1_000_000L, "1 MB")]
    [InlineData(1_024L, "1 KiB")]
    [InlineData(500L, "500 B")]
    [InlineData(0L, "0")]
    public void ToString_UsesTheLargestUnitAcrossBothFamilies(long bytes, string expected)
    {
        // All the binary units used to be listed before the decimal ones, and the loop returns the
        // first unit whose value is at least 1 — so 1_000_000_000 rendered as "953.67 MiB" and "GB"
        // could never be returned at all.
        Assert.Equal(expected, new ByteSize(bytes).ToString());
    }

    [Fact]
    public void UpscalerConfig_EnvironmentVariableStyleKeys_Bind()
    {
        // The container deployment path, mirroring how both hosts register it: Program.cs calls
        // AddEnvironmentVariables("Ingest_"), so the prefix is *stripped* and `__` becomes the section
        // separator — Ingest_Upscaler__MemoryBudgetBytes lands on Upscaler:MemoryBudgetBytes. The
        // in-memory key this test used before ("Ingest:Upscaler:MemoryBudgetBytes") binds a section the
        // application never reads, and exercises neither the prefix nor the double-underscore
        // translation the containers depend on.
        const string name = "Ingest_Upscaler__MemoryBudgetBytes";
        string? previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "12 GiB");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables("Ingest_")
                .Build();
            UpscalerConfig config = configuration.GetSection("Upscaler").Get<UpscalerConfig>()!;

            Assert.Equal(12884901888L, config.ResolvedMemoryBudgetBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
