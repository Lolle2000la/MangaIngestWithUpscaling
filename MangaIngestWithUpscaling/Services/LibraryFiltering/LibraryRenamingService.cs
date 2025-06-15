using System.Text.RegularExpressions;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using System.IO;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

[RegisterScoped]
public class LibraryRenamingService : ILibraryRenamingService
{
    public FoundChapter ApplyRenameRules(FoundChapter chapter, IReadOnlyList<LibraryRenameRule> rules)
    {
        var fileName = chapter.FileName;
        var relativePath = chapter.RelativePath;
        var meta = chapter.Metadata;
        var series = meta.Series;
        var chapterTitle = meta.ChapterTitle;

        foreach (var rule in rules)
        {
            string input = rule.TargetField switch
            {
                LibraryRenameTargetField.SeriesTitle => series,
                LibraryRenameTargetField.ChapterTitle => chapterTitle,
                LibraryRenameTargetField.FileName => fileName,
                _ => null
            };
            if (input == null) continue;
            string result = rule.PatternType switch
            {
                LibraryRenamePatternType.Regex => Regex.Replace(input, rule.Pattern, rule.Replacement),
                LibraryRenamePatternType.Contains => input.Replace(rule.Pattern, rule.Replacement),
                _ => input
            };
            if (result != input)
            {
                switch (rule.TargetField)
                {
                    case LibraryRenameTargetField.SeriesTitle:
                        series = result;
                        break;
                    case LibraryRenameTargetField.ChapterTitle:
                        chapterTitle = result;
                        break;
                    case LibraryRenameTargetField.FileName:
                        fileName = result;
                        // adjust relative path
                        var dir = Path.GetDirectoryName(relativePath) ?? string.Empty;
                        relativePath = string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
                        break;
                }
            }
        }

        var newMeta = meta with { Series = series, ChapterTitle = chapterTitle };
        return new FoundChapter(fileName, relativePath, chapter.StorageType, newMeta);
    }
}
