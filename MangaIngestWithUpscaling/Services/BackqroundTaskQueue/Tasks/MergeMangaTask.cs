using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.MangaManagement;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class MergeMangaTask : BaseTask
{
    public override string TaskFriendlyName => MergeMessage;
    

    public int IntoMangaId { get; set; }
    
    public List<int> ToMerge { get; set; }

    public string MergeMessage { get; set; }
    
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    [Obsolete("Is your name JsonSerializer? No, I didn't think so.", true)]
    public MergeMangaTask() {}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    public MergeMangaTask(Manga into, List<Manga> toMerge)
    {
        IntoMangaId = into.Id;
        ToMerge = toMerge.Select(m => m.Id).ToList();
        string pluralSuffix = toMerge.Count > 1 ? "s" : "";
        MergeMessage = $"Merging {toMerge.Count} manga{pluralSuffix} into {into.PrimaryTitle}";
    }

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var merger = services.GetRequiredService<IMangaMerger>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var logger = services.GetRequiredService<ILogger<MergeMangaTask>>();
        var into = await dbContext.MangaSeries.FindAsync(IntoMangaId, cancellationToken);
        var toMerge = await dbContext.MangaSeries.Where(m => ToMerge.Contains(m.Id)).ToListAsync(cancellationToken);

        if (into == null)
        {
            logger.LogCritical("Could not find manga to merge into with the id of {IntoMangaId}", IntoMangaId);
            return;
        }

        if (toMerge.Count == 0)
        {
            logger.LogCritical("Could not find any of the mangas to merge into {IntoMangaId}: {ToMerge}", IntoMangaId, ToMerge);
            return;
        }

        if (toMerge.Count != ToMerge.Count)
        {
            logger.LogWarning("Could not find some of the mangas to merge into {IntoMangaId}: {ToMergeMissing}", 
                IntoMangaId, ToMerge.Where(t => !toMerge.Select(m => m.Id).Contains(t)));
        }

        await merger.MergeAsync(into, toMerge, cancellationToken);
    }

    public override int RetryFor { get; set; } = 1;
}
