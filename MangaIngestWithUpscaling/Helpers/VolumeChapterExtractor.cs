using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Represents a parsed result with volume and/or chapter information.
/// </summary>
public class VolumeChapter
{
    /// <summary>
    /// The volume number, or null if not present.
    /// </summary>
    public double? Volume { get; set; }
    /// <summary>
    /// The chapter number, or null if not present.
    /// </summary>
    public double? Chapter { get; set; }
}

/// <summary>
/// Provides methods to extract volume and chapter numbers from chapter strings.
/// </summary>
public static partial class VolumeChapterExtractor
{
    // Regex pattern breakdown:
    // - The volume part is optional. It supports:
    //   * Western keywords: "Volume", "Vol.", or "V." followed by a number (with optional fractional part)
    //   * East Asian: the prefix "第", a number, and a suffix "巻" or "卷"
    // - The chapter part is optional. It supports:
    //   * Western keywords: "Chapter", "Kapitel", "Chapitre", "Capítulo", or "Capitolo" followed by a number (with optional fractional part)
    //   * East Asian: the prefix "第", a number, and a suffix "話" or "章"
    // At least one of the parts must be present.
    const string pattern = @"
            (?:
                (?<vol>
                    (?:
                        (?:Volume|Vol\.|V\.?)\s*(?<volnum1>[0-9０-９]+(?:[.\-][0-9０-９]+)?)   # Western volume
                        |
                        第(?<volnum2>[0-9０-９]+(?:[.\-][0-9０-９]+)?)(?:巻|卷)                # East Asian volume
                    )
                )
            )?
            \s*
            (?:
                (?<chap>
                    (?:
                        (?:Chapter|Kapitel|Chapitre|Capítulo|Capitolo)\s*(?<chapnum1>[0-9０-９]+(?:[.\-][0-9０-９]+)?)   # Western chapter
                        |
                        第(?<chapnum2>[0-9０-９]+(?:[.\-][0-9０-９]+)?)(?:話|章)                                      # East Asian chapter
                    )
                )
            )?";

    [GeneratedRegex(pattern, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace, "ja-JP")]
    private static partial Regex GetVolumeChapterRegex();

    /// <summary>
    /// Extracts volume and chapter numbers from a string.
    /// Supported formats include:
    /// - Western: "Volume 1 Chapter 2", "Chapter 3.5", "Vol. 1"
    /// - East Asian: "第１巻", "第４話-5" (volume uses "巻" or "卷", chapter uses "話" or "章")
    /// At least one of volume or chapter must be present.
    /// </summary>
    /// <param name="input">The input string to parse.</param>
    /// <returns>
    /// A <see cref="VolumeChapter"/> object with the parsed numbers, or null if neither volume nor chapter is found.
    /// </returns>
    public static VolumeChapter? ExtractVolumeChapter(string input)
    {
        

        // Use RegexOptions.IgnorePatternWhitespace to allow inline comments and RegexOptions.IgnoreCase for keywords.
        var match = GetVolumeChapterRegex().Match(input.Trim());
        if (!match.Success || (!match.Groups["vol"].Success && !match.Groups["chap"].Success))
        {
            return null;
        }

        double? volume = null;
        double? chapter = null;

        // Parse volume if available.
        if (match.Groups["vol"].Success)
        {
            string volStr = match.Groups["volnum1"].Success
                ? match.Groups["volnum1"].Value
                : (match.Groups["volnum2"].Success ? match.Groups["volnum2"].Value : null);
            if (!string.IsNullOrEmpty(volStr))
            {
                volStr = ConvertFullWidthToHalfWidth(volStr);
                if (double.TryParse(volStr, out double volParsed))
                {
                    volume = volParsed;
                }
            }
        }

        // Parse chapter if available.
        if (match.Groups["chap"].Success)
        {
            string chapStr = match.Groups["chapnum1"].Success
                ? match.Groups["chapnum1"].Value
                : (match.Groups["chapnum2"].Success ? match.Groups["chapnum2"].Value : null);
            if (!string.IsNullOrEmpty(chapStr))
            {
                chapStr = ConvertFullWidthToHalfWidth(chapStr);
                if (double.TryParse(chapStr, out double chapParsed))
                {
                    chapter = chapParsed;
                }
            }
        }

        return new VolumeChapter { Volume = volume, Chapter = chapter };
    }

    /// <summary>
    /// Converts full-width digits to standard half-width digits.
    /// </summary>
    /// <param name="input">A string that may contain full-width digits.</param>
    /// <returns>A string where full-width digits are replaced by half-width digits.</returns>
    public static string ConvertFullWidthToHalfWidth(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            // Convert full-width digit (Unicode '０' to '９') to ASCII digit.
            if (ch >= '０' && ch <= '９')
            {
                sb.Append((char)(ch - '０' + '0'));
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
