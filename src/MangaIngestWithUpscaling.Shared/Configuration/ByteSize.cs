using System.Globalization;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Shared.Configuration;

/// <summary>
/// A number of bytes, parsed from a configuration string such as <c>1GiB</c>, <c>512 MiB</c>,
/// <c>350MB</c> or a plain byte count.
/// <para>
/// Decimal suffixes are powers of 1000 and binary ones powers of 1024, so <c>1GiB</c> is exactly
/// 1073741824 while <c>1GB</c> is exactly 1000000000. Offering both matters: this app's memory
/// arithmetic is binary, and pasting the wrong unit into a configuration file should not silently
/// mean a different number.
/// </para>
/// </summary>
public readonly record struct ByteSize : IComparable<ByteSize>
{
    /// <summary>Matches a number and an optional unit, e.g. <c>1.5 GiB</c>.</summary>
    private static readonly Regex Pattern = new(
        @"^(?<value>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>[a-zA-Z]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // An empty unit and "b" are plain bytes, which is what existing configuration files use.
    private static readonly Dictionary<string, double> Multipliers = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [""] = 1d,
        ["b"] = 1d,
        ["kb"] = 1_000d,
        ["kib"] = 1_024d,
        ["mb"] = 1_000_000d,
        ["mib"] = 1_048_576d,
        ["gb"] = 1_000_000_000d,
        ["gib"] = 1_073_741_824d,
        ["tb"] = 1_000_000_000_000d,
        ["tib"] = 1_099_511_627_776d,
        ["pb"] = 1_000_000_000_000_000d,
        ["pib"] = 1_125_899_906_842_624d,
    };

    private static readonly (string Suffix, double Size)[] LargestFirst =
    [
        ("PiB", 1_125_899_906_842_624d),
        ("TiB", 1_099_511_627_776d),
        ("GiB", 1_073_741_824d),
        ("MiB", 1_048_576d),
        ("KiB", 1_024d),
        ("PB", 1_000_000_000_000_000d),
        ("TB", 1_000_000_000_000d),
        ("GB", 1_000_000_000d),
        ("MB", 1_000_000d),
        ("KB", 1_000d),
    ];

    public ByteSize(long bytes) => Bytes = bytes;

    public long Bytes { get; init; }

    public static ByteSize Parse(string text) =>
        TryParse(text, out ByteSize result)
            ? result
            : throw new FormatException(
                $"'{text}' is not a byte size. Expected a number with an optional unit, for example 1073741824, 512MiB or 1.5 GiB."
            );

    public static bool TryParse(string? text, out ByteSize result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = Pattern.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!Multipliers.TryGetValue(match.Groups["unit"].Value, out double multiplier))
        {
            return false;
        }

        if (
            !double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value
            )
        )
        {
            return false;
        }

        double bytes = value * multiplier;
        if (double.IsNaN(bytes) || double.IsInfinity(bytes) || bytes is < 0 or > long.MaxValue)
        {
            return false;
        }

        result = new ByteSize((long)Math.Round(bytes));
        return true;
    }

    /// <summary>Formats with the largest unit that keeps the value at least 1, e.g. <c>1 GiB</c>.</summary>
    public override string ToString()
    {
        if (Bytes == 0)
        {
            return "0";
        }

        foreach (var (suffix, size) in LargestFirst)
        {
            double scaled = Bytes / size;
            if (scaled >= 1)
            {
                string text =
                    Math.Abs(scaled - Math.Floor(scaled)) < 0.005
                        ? scaled.ToString("0", CultureInfo.InvariantCulture)
                        : scaled.ToString("0.##", CultureInfo.InvariantCulture);
                return $"{text} {suffix}";
            }
        }

        return $"{Bytes} B";
    }

    public int CompareTo(ByteSize other) => Bytes.CompareTo(other.Bytes);

    public static implicit operator long(ByteSize size) => size.Bytes;

    public static implicit operator ByteSize(long bytes) => new(bytes);
}
