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

    [Fact]
    public void UpscalerConfig_EnvironmentVariableStyleKeys_Bind()
    {
        // The container deployment path: Ingest_Upscaler__<Property>
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Ingest:Upscaler:MemoryBudgetBytes"] = "12 GiB" }
            )
            .Build();
        UpscalerConfig config = configuration.GetSection("Ingest:Upscaler").Get<UpscalerConfig>()!;

        Assert.Equal(12884901888L, config.ResolvedMemoryBudgetBytes);
    }
}
