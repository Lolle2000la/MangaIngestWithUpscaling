using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

[RegisterScoped]
public class LibraryFilteringService : ILibraryFilteringService
{
    public bool FilterChapter(FoundChapter chapter, IEnumerable<LibraryFilterRule> libraryFilterRules)
    {
        List<LibraryFilterRule> rules = libraryFilterRules.ToList();
        if (!rules.Any())
        {
            return true;
        }

        Func<LibraryFilterRule, bool> checkMatch = rule =>
        {
            string? targetFieldData = rule.TargetField switch
            {
                LibraryFilterTargetField.MangaTitle => chapter.Metadata.Series,
                LibraryFilterTargetField.ChapterTitle => chapter.Metadata.ChapterTitle,
                LibraryFilterTargetField.FilePath => chapter.RelativePath,
                _ => throw new NotImplementedException()
            };

            if (targetFieldData == null)
            {
                return false;
            }

            return rule.PatternType switch
            {
                LibraryFilterPatternType.Contains => targetFieldData.Contains(rule.Pattern),
                LibraryFilterPatternType.Regex => Regex.IsMatch(targetFieldData, rule.Pattern),
                _ => throw new NotImplementedException()
            };
        };

        if (rules.Where(r => r.Action == FilterAction.Exclude).Any(checkMatch))
        {
            return false;
        }

        IEnumerable<LibraryFilterRule> includeRules = rules.Where(r => r.Action == FilterAction.Include);
        if (includeRules.Any() && !includeRules.Any(checkMatch))
        {
            return false;
        }

        return true;
    }
}