using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Implements a natural sort comparer for strings.
/// Numeric parts are compared as numbers while non-numeric parts are compared alphabetically.
/// </summary>
public partial class NaturalSortComparer<T>(Func<T, string> propSelector) : IComparer<T>
{
    // Regex to split the string into groups of digits or non-digits.
    private static readonly Regex _regex = SplitIntoPartsRegex();

    /// <summary>
    /// Compares two strings using natural sort order.
    /// </summary>
    /// <param name="x">The first string to compare.</param>
    /// <param name="y">The second string to compare.</param>
    /// <returns>
    /// A negative number if x is less than y, zero if they are equal,
    /// or a positive number if x is greater than y.
    /// </returns>
    public int Compare(T? x, T? y)
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

        string xProp = propSelector(x);
        string yProp = propSelector(y);

        if (xProp == yProp)
        {
            return 0;
        }

        // Split both strings into numeric and non-numeric parts.
        var xParts = _regex.Matches(xProp);
        var yParts = _regex.Matches(yProp);

        int minParts = Math.Min(xParts.Count, yParts.Count);
        for (int i = 0; i < minParts; i++)
        {
            string xPart = xParts[i].Value;
            string yPart = yParts[i].Value;

            int result;
            // Try to parse both parts as integers.
            if (double.TryParse(xPart, out double xNum) && double.TryParse(yPart, out double yNum))
            {
                result = xNum.CompareTo(yNum);
            }
            else
            {
                // Compare text parts case-insensitively.
                result = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        // If all compared parts are equal, the string with more parts comes later.
        return xParts.Count.CompareTo(yParts.Count);
    }

    [GeneratedRegex(@"(\d+?\.?\d*)|(\D+?\.?\D*)", RegexOptions.Compiled)]
    private static partial Regex SplitIntoPartsRegex();
}
