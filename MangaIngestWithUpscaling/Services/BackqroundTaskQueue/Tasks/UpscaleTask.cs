﻿using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.FileSystem;
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
        await upscaler.Upscale(currentStoragePath, upscaleTargetPath, upscalerProfile, cancellationToken);

        chapter.IsUpscaled = true;
        chapter.UpscalerProfile = upscalerProfile;
        chapter.UpscalerProfileId = upscalerProfile.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public override int RetryFor { get; set; } = 1;
}
