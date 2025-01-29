using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.ChapterRecognition;
using Microsoft.IdentityModel.Tokens;
using System.Text.RegularExpressions;

namespace MangaIngestWithUpscaling.Services.LibraryFiltering;

public class LibraryFilteringService : ILibraryFilteringService
{
    public List<FoundChapter> FilterChapters(List<FoundChapter> chapters, IEnumerable<LibraryFilterRule> libraryFilterRules)
    {
        var filteredChapters = new List<FoundChapter>();

        foreach (var chapter in chapters)
        {
            var accepted = libraryFilterRules.Select(rule =>
            {
                var targetFieldData = rule.TargetField switch
                {
                    LibraryFilterTargetField.MangaTitle => chapter.Metadata.ChapterTitle,
                    LibraryFilterTargetField.ChapterTitle => chapter.Metadata.ChapterTitle,
                    LibraryFilterTargetField.FilePath => chapter.RelativePath
                };

                var ruleMatched = rule.PatternType switch
                {
                    LibraryFilterPatternType.Containes => targetFieldData.Contains(rule.Pattern),
                    LibraryFilterPatternType.Regex => Regex.IsMatch(targetFieldData, rule.Pattern)
                };

                // If the action is include, then return true if the rule is matched, but if it is exclude,
                // then return false, since we want want to exclude what was matched here.
                return rule.Action == FilterAction.Include ? ruleMatched : !ruleMatched;
            })
                // Ensure all rules are satisfied.
                .Aggregate(true, (current, next) => current && next);

            if (accepted)
            {
                filteredChapters.Add(chapter);
            }
        }
        return filteredChapters;

    }
}
