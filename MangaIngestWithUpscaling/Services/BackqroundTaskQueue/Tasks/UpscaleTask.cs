﻿using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.FileSystem;
using MangaIngestWithUpscaling.Services.MetadataHandling;
using MangaIngestWithUpscaling.Services.Upscaling;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;

public class UpscaleTask : BaseTask
{
    public override string TaskFriendlyName => FriendlyEntryName;

    public int UpscalerProfileId { get; set; }
    public int ChapterId { get; set; }

    public string FriendlyEntryName { get; set; } = string.Empty;

    public UpscaleTask() { }
    public UpscaleTask(Chapter chapter, UpscalerProfile profile)
    {
        ChapterId = chapter.Id;
        UpscalerProfileId = profile.Id;
        FriendlyEntryName = $"Upscaling {chapter.FileName} with {profile.Name}";
    }

    public override async Task ProcessAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<UpscaleTask>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var metadataChanger = services.GetRequiredService<IMangaMetadataChanger>();
        var chapter = await dbContext.Chapters
            .Include(c => c.Manga)
            .ThenInclude(m => m.Library)
            .ThenInclude(l => l.UpscalerProfile)
            .Include(c => c.UpscalerProfile)
            .FirstOrDefaultAsync(
            c => c.Id == ChapterId, cancellationToken: cancellationToken);
        var upscalerProfile = await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
            c => c.Id == UpscalerProfileId, cancellationToken: cancellationToken);

        if (chapter == null || upscalerProfile == null)
        {
            throw new InvalidOperationException("Chapter or upscaler profile not found.");
        }

        FriendlyEntryName = $"Upscaling {chapter.FileName} with {upscalerProfile.Name}";


        if (chapter.IsUpscaled && chapter.UpscalerProfile?.Id == upscalerProfile.Id)
        {
            logger.LogInformation($"Chapter {chapter.FileName} is already upscaled with {upscalerProfile.Name}");
            return;
        }

        if (chapter.Manga.Library.UpscaledLibraryPath == null)
        {
            throw new InvalidOperationException("Upscaled library path not set.");
        }

        string upscaleTargetPath = Path.Combine(chapter.Manga.Library.UpscaledLibraryPath, chapter.RelativePath);
        //if (!Directory.Exists(Path.GetDirectoryName(upscaleBasePath)))
        //{
        //    Directory.CreateDirectory(upscaleBasePath);
        //}

        string currentStoragePath = Path.Combine(chapter.Manga.Library.NotUpscaledLibraryPath, chapter.RelativePath);

        var upscaler = services.GetRequiredService<IUpscaler>();
        try
        {
            await upscaler.Upscale(currentStoragePath, upscaleTargetPath, upscalerProfile, cancellationToken);
        }
        catch (Exception)
        {
            if (File.Exists(upscaleTargetPath))
            {
                File.Delete(upscaleTargetPath);
            }
            throw;
        }

        // save the manga title to see if it has changed in the meantime
        string oldMangaTitle = chapter.Manga.PrimaryTitle;

        // reload the chapter and manga from db to see if the title has changed in the meantime
        await dbContext.Entry(chapter).ReloadAsync();
        await dbContext.Entry(chapter.Manga).ReloadAsync();

        chapter.IsUpscaled = true;
        chapter.UpscalerProfile = upscalerProfile;
        chapter.UpscalerProfileId = upscalerProfile.Id;
        dbContext.Update(chapter);
        await dbContext.SaveChangesAsync();

        // make sure that the new chapter is applied
        if (oldMangaTitle != chapter.Manga.PrimaryTitle)
        {
            metadataChanger.ApplyUpscaledChapterTitle(chapter, chapter.Manga.PrimaryTitle, upscaleTargetPath);
        }
    }

    public override int RetryFor { get; set; } = 1;
}
