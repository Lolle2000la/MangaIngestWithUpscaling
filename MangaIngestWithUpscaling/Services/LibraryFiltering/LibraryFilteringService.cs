using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.ChapterRecognition;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

[RegisterScoped]
public class LibraryFilteringService : ILibraryFilteringService
{
    public bool FilterChapter(FoundChapter chapter, IEnumerable<LibraryFilterRule> libraryFilterRules)
    {
        bool accepted = libraryFilterRules.Select(rule =>
            {
                string? targetFieldData = rule.TargetField switch
                {
                    LibraryFilterTargetField.MangaTitle => chapter.Metadata.Series,
                    LibraryFilterTargetField.ChapterTitle => chapter.Metadata.ChapterTitle,
                    LibraryFilterTargetField.FilePath => chapter.RelativePath,
                    _ => throw new NotImplementedException()
                };

                bool ruleMatched = false;

                if (targetFieldData != null)
                    ruleMatched = rule.PatternType switch
                    {
                        LibraryFilterPatternType.Contains => targetFieldData.Contains(rule.Pattern),
                        LibraryFilterPatternType.Regex => Regex.IsMatch(targetFieldData, rule.Pattern),
                        _ => throw new NotImplementedException()
                    };

                // If the action is include, then return true if the rule is matched, but if it is exclude,
                // then return false, since we want want to exclude what was matched here.
                return rule.Action == FilterAction.Include ? ruleMatched : !ruleMatched;
            })
            // Ensure all rules are satisfied.
            .Aggregate(true, (current, next) => current && next);

        return accepted;
    }
}