using System.Text.RegularExpressions;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using MangaIngestWithUpscaling.Services.LibraryFiltering;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

[RegisterScoped]
public class LibraryRenamingService : ILibraryRenamingService
{
    private readonly ILogger<LibraryRenamingService> _logger;

    public LibraryRenamingService(ILogger<LibraryRenamingService> logger)
    {
        _logger = logger;
    }

    public FoundChapter ApplyRenameRules(FoundChapter chapter, IReadOnlyList<LibraryRenameRule> rules)
    {
        var fileName = chapter.FileName;
        var relativePath = chapter.RelativePath;
        var meta = chapter.Metadata;
        var series = meta.Series;
        var chapterTitle = meta.ChapterTitle;

        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Pattern))
            {
                continue; // Skip rules with empty patterns
            }

            string? currentInput = rule.TargetField switch
            {
                LibraryRenameTargetField.SeriesTitle => series,
                LibraryRenameTargetField.ChapterTitle => chapterTitle,
                LibraryRenameTargetField.FileName => fileName,
                _ => null
            };

            if (currentInput == null) continue;

            string result = currentInput; // Default to original input

            try
            {
                result = rule.PatternType switch
                {
                    LibraryRenamePatternType.Regex => Regex.Replace(currentInput, rule.Pattern, rule.Replacement ?? string.Empty),
                    LibraryRenamePatternType.Contains => currentInput.Replace(rule.Pattern, rule.Replacement ?? string.Empty),
                    _ => currentInput
                };
            }
            catch (ArgumentException ex)
            {
                // Log the error and skip applying this problematic rule for the preview
                _logger.LogWarning(ex, "Invalid pattern or argument in rename rule. Pattern: '{Pattern}', Replacement: '{Replacement}'", rule.Pattern, rule.Replacement);
                // result remains currentInput, effectively skipping the rule
            }
            
            result = result.Trim();

            if (result != currentInput)
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
