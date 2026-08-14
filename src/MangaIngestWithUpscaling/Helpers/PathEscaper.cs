using System.Text;

namespace MangaIngestWithUpscaling.Helpers;

public static class PathEscaper
{
    private static List<char> invalidChars = Path.GetInvalidFileNameChars()
        .Concat(Path.GetInvalidPathChars())
        .Distinct()
        .ToList();

    /// <summary>
    /// Escapes forbidden characters in a filesystem file name by percent-encoding them.
    /// </summary>
    /// <param name="fileName">The file name to escape</param>
    /// <returns>A new string with forbidden characters escaped.</returns>
    public static string EscapeFileName(string fileName)
    {
        // Retrieve the set of characters not allowed in a file name.
        StringBuilder sb = new StringBuilder();
        foreach (char c in fileName)
        {
            if (invalidChars.Contains(c))
            {
                // Escape the forbidden character using percent-encoding.
                sb.Append($"%{(int)c:X2}");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a file name for use as a directory segment, additionally neutralizing
    /// segments that would resolve to the current (".") or parent ("..") directory, or
    /// that the OS would collapse to an empty name by trimming trailing dots and spaces
    /// (e.g. "..." or ".. "). The caller's metadata keeps the original title unchanged.
    /// </summary>
    /// <param name="fileName">The directory name to escape.</param>
    /// <returns>A safe, non-empty directory segment.</returns>
    public static string EscapeDirectoryName(string fileName)
    {
        var escaped = EscapeFileName(fileName);
        var trimmed = escaped.TrimEnd(' ', '.');
        if (trimmed.All(c => c == '.'))
        {
            // Percent-encode the dots so the segment can't resolve to "." or "..".
            // Only trailing spaces are dropped (from inputs like ".. "); spaces inside
            // normal titles are left untouched because this branch only runs for
            // dot/space-only segments.
            var encoded = escaped.Replace(".", "%2E").TrimEnd(' ');
            return encoded.Length > 0 ? encoded : "%20";
        }

        return escaped;
    }
}
