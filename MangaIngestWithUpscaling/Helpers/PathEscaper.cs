using System.Text;

namespace MangaIngestWithUpscaling.Helpers;

public static class PathEscaper
{
    private static List<char> invalidChars =
        Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToList();
    
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
}
