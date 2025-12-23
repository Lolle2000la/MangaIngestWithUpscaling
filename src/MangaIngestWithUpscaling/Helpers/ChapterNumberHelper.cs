using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Helper class for extracting and working with chapter numbers from filenames and titles
/// </summary>
public static partial class ChapterNumberHelper
{
    /// <summary>
    /// Extracts the chapter number from a filename or title string
    /// </summary>
    /// <param name="text">The filename or title to extract the chapter number from</param>
    /// <returns>The chapter number as a string, or null if no chapter number is found</returns>
    public static string? ExtractChapterNumber(string text)
    {
        Match match = ChapterNumberRegex().Match(text);
        if (match.Success)
        {
            string mainNumber = match.Groups["num"].Value;
            string subNumber = match.Groups["subnum"].Value;

            if (!string.IsNullOrEmpty(subNumber))
            {
                return $"{mainNumber}.{subNumber}";
            }

            return mainNumber;
        }

        // Also try a simpler pattern that just looks for numbers
        Match simpleMatch = SimpleNumberRegex().Match(text);
        if (simpleMatch.Success)
        {
            return simpleMatch.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Comprehensive regex for extracting chapter numbers from various languages and formats
    /// </summary>
    [GeneratedRegex(
        @"
               # Group 1: Latin, Cyrillic & similar alphabets
               (?:
                 # Core English, German, Russian
                 ch(?:apter)?\.? | kapitel | glava | file | ep(?:isode|\.)?

                 # Romance Languages
                 | cap(?:[íi]tulo|itol|\.)? | chap(?:itre|\.)?

                 # Eastern European Languages
                 | rozdział | kapitola | fejezet | poglavlje | розділ

                 # South-East Asian Languages (Latin script)
                 | chương | bab | kabanata
               )
               \s*(?<num>\d+)(?:[.-](?<subnum>\d+))?


               # Group 2: CJK (Chinese, Japanese, Korean)
               | (?:第|제)
               \s*(?<num>\d+)(?:[.-](?<subnum>\d+))?
               \s*(?:話|章|话|화|장)?


               # Group 3: Other SEA Scripts (Thai, Burmese)
               | (?:บทที่|အခန်း)
               \s*(?<num>\d+)(?:[.-](?<subnum>\d+))?
               ",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
    )]
    public static partial Regex ChapterNumberRegex();

    /// <summary>
    /// Simple fallback regex for extracting numeric patterns
    /// </summary>
    [GeneratedRegex(@"(\d+(?:\.\d+)?)")]
    private static partial Regex SimpleNumberRegex();
}
