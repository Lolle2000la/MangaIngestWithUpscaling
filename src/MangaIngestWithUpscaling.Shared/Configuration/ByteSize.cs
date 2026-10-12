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
    private static readonly Dictionary<string, decimal> Multipliers = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [""] = 1,
        ["b"] = 1,
        ["kb"] = 1_000,
        ["kib"] = 1_024,
        ["mb"] = 1_000_000,
        ["mib"] = 1_048_576,
        ["gb"] = 1_000_000_000,
        ["gib"] = 1_073_741_824,
        ["tb"] = 1_000_000_000_000,
        ["tib"] = 1_099_511_627_776,
        ["pb"] = 1_000_000_000_000_000,
        ["pib"] = 1_125_899_906_842_624,
    };

    // Largest first across *both* families. The loop returns the first unit whose value is at least
    // 1, so listing a whole family before the other makes the later one unreachable: with all the
    // binary units first, 1_000_000_000 rendered as "953.67 MiB" and "GB" could never be returned at
    // all, contradicting the "largest unit" contract below.
    private static readonly (string Suffix, double Size)[] LargestFirst =
    [
        ("PiB", 1_125_899_906_842_624d),
        ("PB", 1_000_000_000_000_000d),
        ("TiB", 1_099_511_627_776d),
        ("TB", 1_000_000_000_000d),
        ("GiB", 1_073_741_824d),
        ("GB", 1_000_000_000d),
        ("MiB", 1_048_576d),
        ("MB", 1_000_000d),
        ("KiB", 1_024d),
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

        if (!Multipliers.TryGetValue(match.Groups["unit"].Value, out decimal multiplier))
        {
            return false;
        }

        if (
            !decimal.TryParse(
                match.Groups["value"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal value
            )
        )
        {
            return false;
        }

        decimal bytes;
        try
        {
            bytes = value * multiplier;
        }
        catch (OverflowException)
        {
            return false;
        }

        // long.MaxValue is not exactly representable as a double, so a double bound compared equal
        // to 2^63 and let "9223372036854775808" through, where the cast wrapped it to a negative
        // value. Decimal holds the boundary exactly, so a value that does not fit a long is rejected
        // like any other bad input.
        if (bytes < 0 || bytes > long.MaxValue)
        {
            return false;
        }

        result = new ByteSize((long)Math.Round(bytes, MidpointRounding.ToEven));
        return true;
    }

    /// <summary>
    /// Formats with the largest unit that keeps the value at least 1, e.g. <c>1 GiB</c>. Both families
    /// are candidates and the largest qualifying one wins, so 1000000000 renders as <c>1 GB</c> and
    /// 1073741824 as <c>1 GiB</c>.
    /// <para>
    /// This is a human-readable rendering, not a lossless encoding: a value just below a boundary
    /// rounds up across it (1048575 bytes renders as "1.05 MB", which parses back higher). The logs
    /// only need it readable, and the whole-number rendering is what makes them so; use
    /// <see cref="Bytes"/> where the exact value matters.
    /// </para>
    /// </summary>
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
