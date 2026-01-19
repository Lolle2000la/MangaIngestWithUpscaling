using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Provides utility methods for validating regular expressions.
/// </summary>
public static class RegexValidator
{
    /// <summary>
    /// Validates whether a string is a valid regular expression pattern.
    /// </summary>
    /// <param name="pattern">The regex pattern to validate.</param>
    /// <returns>True if the pattern is valid; otherwise, false.</returns>
    public static bool IsValidRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        try
        {
            _ = Regex.Match("", pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
