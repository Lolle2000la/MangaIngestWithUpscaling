using System.Globalization;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Compares names naturally: a run of digits compares as a number, so "2" sorts before "10" where
/// an ordinal comparison would put "10" first. Used to order the archive entries that make up a
/// chapter's pages, whose sequence is content-critical.
/// <para>
/// This is the canonical implementation. The main app's <c>NaturalSortComparer&lt;T&gt;</c> is a thin
/// adapter over it (it selects a key and delegates), so the whole-CBZ and streamed paths order pages
/// identically.
/// </para>
/// </summary>
public sealed partial class NaturalEntryNameComparer : IComparer<string>
{
    /// <summary>Shared instance; the comparer is stateless.</summary>
    public static readonly NaturalEntryNameComparer Instance = new();

    /// <summary>
    /// Compares two names, returning a negative number when <paramref name="x" /> sorts before
    /// <paramref name="y" />, zero when they are equal, and a positive number otherwise. The result
    /// is a total order: a mixed pair is decided by kind first (a digit run always sorts before a
    /// non-digit run), so the comparison never depends on which pair is being compared and sorting a
    /// list of names is deterministic.
    /// </summary>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (x == y)
        {
            return 0;
        }

        MatchCollection xParts = PartsRegex().Matches(x);
        MatchCollection yParts = PartsRegex().Matches(y);

        int minParts = Math.Min(xParts.Count, yParts.Count);
        for (int i = 0; i < minParts; i++)
        {
            string xPart = xParts[i].Value;
            string yPart = yParts[i].Value;

            bool xIsNumber = IsNumericPart(xPart);
            bool yIsNumber = IsNumericPart(yPart);

            // Kind decides a mixed pair before value or text do: a digit run always sorts before a
            // non-digit run. These names are overwhelmingly "1", "2", "10", … with the odd "cover" or
            // "extra", so putting the number runs first preserves that page sequence (and matches the
            // ordinal fall-through for letters). Letting the text run's first character decide instead
            // would make the order depend on the pair — a text run such as "NaN" is accepted by the
            // numeric parser, so the same run would compare differently against a number and against
            // another text run, and the comparison would no longer be a total order.
            int result;
            if (xIsNumber && yIsNumber)
            {
                // A digit run compares as a number, so "2" precedes "10" where ordinal puts "10" first.
                result = double.Parse(xPart, NumberStyles.Any, CultureInfo.InvariantCulture)
                    .CompareTo(double.Parse(yPart, NumberStyles.Any, CultureInfo.InvariantCulture));
            }
            else if (xIsNumber)
            {
                result = -1;
            }
            else if (yIsNumber)
            {
                result = 1;
            }
            else
            {
                result = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }

            // Equal runs — numerically equal ("1" vs "01") or differing only in case ("A" vs "a"):
            // break the tie ordinally. Without it the two compare equal, and a consumer that treats
            // this as a total order — a set, a distinct, a sorted dictionary — would treat two
            // different pages as one and lose a page.
            result = string.Compare(xPart, yPart, StringComparison.Ordinal);
            if (result != 0)
            {
                return result;
            }
        }

        // Equal up to the shorter name: the longer one sorts after it.
        return xParts.Count.CompareTo(yParts.Count);
    }

    /// <summary>
    /// Whether a run is a digit run. The regex splits a name into digit runs (which start with a
    /// digit) and non-digit runs, so the first character decides it. Classifying by
    /// <see cref="double.TryParse(string, out double)" /> instead would accept a text run such as
    /// "NaN" or "Infinity" as numeric, which makes a mixed pair compare differently depending on
    /// which non-digit run it is paired with and breaks the total order.
    /// </summary>
    private static bool IsNumericPart(string part) => char.IsAsciiDigit(part[0]);

    [GeneratedRegex(@"(\d+?\.?\d*)|(\D+?\.?\D*)", RegexOptions.Compiled)]
    private static partial Regex PartsRegex();
}
