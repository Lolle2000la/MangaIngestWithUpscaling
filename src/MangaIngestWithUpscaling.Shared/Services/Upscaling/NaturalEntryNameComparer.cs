using System.Globalization;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Compares names naturally: a run of digits compares as a number, so "2" sorts before "10" where
/// an ordinal comparison would put "10" first. Used to order the archive entries that make up a
/// chapter's pages, whose sequence is content-critical.
/// <para>
/// The main app has an equivalent comparer, but the Shared project cannot reference it, so this is
/// the local copy rather than a shared one — a wrong-direction project reference would be worse.
/// </para>
/// </summary>
public sealed partial class NaturalEntryNameComparer : IComparer<string>
{
    /// <summary>Shared instance; the comparer is stateless.</summary>
    public static readonly NaturalEntryNameComparer Instance = new();

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

            int result;
            if (
                double.TryParse(
                    xPart,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double xNumber
                )
                && double.TryParse(
                    yPart,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double yNumber
                )
            )
            {
                result = xNumber.CompareTo(yNumber);
            }
            else
            {
                result = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }

            // Numerically equal but textually different ("1" vs "01"): the digits are part of the
            // name, so break the tie ordinally. Without it the two compare equal, and a consumer that
            // treats this as a total order — a set, a distinct, a sorted dictionary — would treat two
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

    [GeneratedRegex(@"(\d+?\.?\d*)|(\D+?\.?\D*)", RegexOptions.Compiled)]
    private static partial Regex PartsRegex();
}
