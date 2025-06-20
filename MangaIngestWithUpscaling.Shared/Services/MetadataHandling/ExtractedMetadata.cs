﻿using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Shared.Services.MetadataHandling;

/// <summary>
/// Represents metadata extracted from a ComicInfo.xml file.
/// </summary>
/// <param name="Series">The name of the series this chapter belongs to.</param>
/// <param name="ChapterTitle">The title of the chapter, usually something like "Chapter 1" or "第１話".</param>
public partial record ExtractedMetadata(string Series, string? ChapterTitle, string? Number)
{

    /// <summary>
    /// Checks if the metadata is correct and corrects it if possible.
    /// </summary>
    /// <returns>The corrected metadata.</returns>
    public ExtractedMetadata CheckAndCorrect()
    {
        string? correctedNumber = Number;
        // compare the chapter number extracted from the title with the number extracted from the metadata
        if (TryExtractChapterNumber(out var chapterNum))
        {
            bool numIsValidNumber = decimal.TryParse(Number, out var num);
            if (numIsValidNumber && decimal.Abs(chapterNum - num) > 0)
            {
                return this with { Number = chapterNum.ToString() };
            }
            else if (!numIsValidNumber)
            {
                correctedNumber = chapterNum.ToString();
            }
        }

        return this with { Number = correctedNumber };
    }

    [GeneratedRegex(@"(?:Chapter\s*(?'num'\d+\.?\d*)|第(?'num'\d+\.?\d*)(?:話|章)(?:(?:-|ー|－)(?'subnum'\d*))?|Kapitel\s*(?'num'\d+\.?\d*))")]
    private static partial Regex ChapterNumExtract();

    private bool TryExtractChapterNumber(out decimal chapterNum)
    {
        if (ChapterTitle == null)
        {
            chapterNum = -1;
            return false;
        }

        var match = ChapterNumExtract().Match(ChapterTitle);
        if (!match.Success)
        {
            chapterNum = -1;
            return false;
        }
        var num = match.Groups["num"].Value;
        var subnum = match.Groups["subnum"].Value;
        if (string.IsNullOrEmpty(subnum))
        {
            return decimal.TryParse(num, out chapterNum);
        }
        return decimal.TryParse($"{num}.{subnum}", out chapterNum);
    }
};
