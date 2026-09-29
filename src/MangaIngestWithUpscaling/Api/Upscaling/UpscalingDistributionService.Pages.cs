using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.EntityFrameworkCore;
using SharedCompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using SharedScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using SharedUpscalerMethod = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerMethod;
using SharedUpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.Api.Upscaling;

/// <summary>
/// Page-streaming half of the distribution service: a worker fetches a chapter's source pages and
/// uploads each upscaled page as it finishes, and the server spools them and assembles the final
/// CBZ once every page is present.
/// </summary>
public partial class UpscalingDistributionService
{
    private static readonly TimeSpan SpoolRetention = TimeSpan.FromHours(24);

    public override async Task<PageManifestResponse> GetPageManifest(
        PageManifestRequest request,
        ServerCallContext context
    )
    {
        PageContext? pageContext = await ResolvePageContextAsync(
            request.TaskId,
            context.CancellationToken
        );
        if (pageContext is null)
        {
            context.Status = new Status(StatusCode.NotFound, "Task, chapter or profile not found");
            return new PageManifestResponse { TaskId = request.TaskId };
        }

        pageStreamSpool.SweepStale(SpoolRetention);
        PageStreamSession session = pageStreamSpool.GetOrCreateSession(
            pageContext.Task.Id,
            pageContext.Identity,
            pageContext.Pages.Count
        );

        // Every page was already spooled by a previous run (e.g. assembly failed transiently):
        // finish the chapter instead of asking the worker to upscale it again.
        if (pageStreamSpool.IsComplete(session))
        {
            try
            {
                await AssembleUpscaledChapterAsync(pageContext, session);
                pageStreamSpool.Remove(pageContext.Task.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to assemble an already-complete page stream for task {TaskId}.",
                    pageContext.Task.Id
                );
            }

            return new PageManifestResponse
            {
                TaskId = pageContext.Task.Id,
                TaskIdentity = pageContext.Identity,
                TaskType = TaskType.Upscale,
                Complete = true,
            };
        }

        var response = new PageManifestResponse
        {
            TaskId = pageContext.Task.Id,
            TaskIdentity = pageContext.Identity,
            TaskType = TaskType.Upscale,
            UpscalerProfile = ToProtoProfile(pageContext.Profile),
        };
        response.Pages.AddRange(
            pageContext.Pages.Select(p => new PageDescriptor
            {
                Index = p.Index,
                SourceName = p.SourceName,
                OutputName = p.OutputName,
            })
        );
        response.CompletedPages.AddRange(pageStreamSpool.GetCompletedPages(session));
        return response;
    }

    public override async Task GetPages(
        GetPagesRequest request,
        IServerStreamWriter<PageChunk> responseStream,
        ServerCallContext context
    )
    {
        PageContext? pageContext = await ResolvePageContextAsync(
            request.TaskId,
            context.CancellationToken
        );
        if (pageContext is null)
        {
            context.Status = new Status(StatusCode.NotFound, "Task, chapter or profile not found");
            return;
        }

        if (!string.Equals(pageContext.Identity, request.TaskIdentity, StringComparison.Ordinal))
        {
            context.Status = new Status(
                StatusCode.FailedPrecondition,
                "The chapter or profile changed; request a new manifest"
            );
            return;
        }

        Dictionary<int, SpoolPageDescriptor> byIndex = pageContext.Pages.ToDictionary(p => p.Index);
        using ZipArchive archive = ZipFile.OpenRead(pageContext.SourcePath);
        Dictionary<string, ZipArchiveEntry> entries = archive.Entries.ToDictionary(
            e => e.FullName,
            StringComparer.Ordinal
        );

        foreach (int pageIndex in request.PageIndexes)
        {
            if (
                !byIndex.TryGetValue(pageIndex, out SpoolPageDescriptor? page)
                || !entries.TryGetValue(page.SourceName, out ZipArchiveEntry? entry)
            )
            {
                continue;
            }

            await using Stream input = entry.Open();
            byte[] buffer = new byte[1024 * 1024];
            int chunkNumber = 0;
            int bytesRead;
            while (
                (
                    bytesRead = await input.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        context.CancellationToken
                    )
                ) > 0
            )
            {
                await responseStream.WriteAsync(
                    new PageChunk
                    {
                        TaskId = request.TaskId,
                        PageIndex = pageIndex,
                        ChunkNumber = chunkNumber++,
                        Chunk = ByteString.CopyFrom(buffer, 0, bytesRead),
                        ContentIdentity = pageContext.Identity,
                    }
                );
            }

            // Empty terminating chunk so the worker knows the page ended.
            await responseStream.WriteAsync(
                new PageChunk
                {
                    TaskId = request.TaskId,
                    PageIndex = pageIndex,
                    ChunkNumber = chunkNumber,
                    Chunk = ByteString.Empty,
                    IsLast = true,
                    ContentIdentity = pageContext.Identity,
                }
            );
        }

        context.Status = new Status(StatusCode.OK, "Pages sent");
    }

    public override async Task<UploadPageResponse> UploadPage(
        IAsyncStreamReader<UploadPageChunk> requestStream,
        ServerCallContext context
    )
    {
        int taskId = 0;
        int pageIndex = 0;
        string identity = string.Empty;
        using var buffer = new MemoryStream();

        await foreach (
            UploadPageChunk chunk in requestStream.ReadAllAsync(context.CancellationToken)
        )
        {
            taskId = chunk.TaskId;
            pageIndex = chunk.PageIndex;
            if (!string.IsNullOrEmpty(chunk.ContentIdentity))
            {
                identity = chunk.ContentIdentity;
            }

            if (!chunk.Chunk.IsEmpty)
            {
                buffer.Write(chunk.Chunk.Span);
            }
        }

        if (taskId == 0)
        {
            return new UploadPageResponse
            {
                Success = false,
                Message = "No page data received",
                Terminal = true,
            };
        }

        // Use the service token for the terminal/assembly work so a worker disconnect cannot
        // cancel it once the final page has been received.
        PageContext? pageContext = await ResolvePageContextAsync(taskId, CancellationToken.None);
        if (pageContext is null)
        {
            return new UploadPageResponse
            {
                Success = false,
                Message = "Task, chapter or profile not found",
                TaskId = taskId,
                PageIndex = pageIndex,
                Terminal = true,
            };
        }

        if (!string.Equals(pageContext.Identity, identity, StringComparison.Ordinal))
        {
            return new UploadPageResponse
            {
                Success = false,
                Message = "The chapter or profile changed; restart the chapter",
                TaskId = taskId,
                PageIndex = pageIndex,
            };
        }

        PageStreamSession session = pageStreamSpool.GetOrCreateSession(
            taskId,
            pageContext.Identity,
            pageContext.Pages.Count
        );

        try
        {
            buffer.Position = 0;
            await pageStreamSpool.WritePageAsync(
                session,
                pageIndex,
                buffer,
                context.CancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to store page {PageIndex} for task {TaskId}",
                pageIndex,
                taskId
            );
            return new UploadPageResponse
            {
                Success = false,
                Message = ex.Message,
                TaskId = taskId,
                PageIndex = pageIndex,
            };
        }

        if (!pageStreamSpool.IsComplete(session))
        {
            return new UploadPageResponse
            {
                Success = true,
                Message = "Page stored",
                TaskId = taskId,
                PageIndex = pageIndex,
            };
        }

        try
        {
            await AssembleUpscaledChapterAsync(pageContext, session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assemble upscaled chapter for task {TaskId}", taskId);
            return new UploadPageResponse
            {
                Success = false,
                Message = ex.Message,
                TaskId = taskId,
                PageIndex = pageIndex,
            };
        }

        pageStreamSpool.Remove(taskId);
        return new UploadPageResponse
        {
            Success = true,
            Message = "Chapter upscaled",
            TaskId = taskId,
            PageIndex = pageIndex,
        };
    }

    private async Task AssembleUpscaledChapterAsync(
        PageContext pageContext,
        PageStreamSession session
    )
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling");
        fileSystem.CreateDirectory(tempDir);
        string tempCbz = Path.Combine(
            tempDir,
            $"upscaled_{pageContext.Task.Id}_{Guid.NewGuid():N}.cbz"
        );

        try
        {
            pageStreamSpool.Assemble(session, pageContext.SourcePath, pageContext.Pages, tempCbz);
            await upscalerJsonHandlingService.WriteUpscalerJsonAsync(
                tempCbz,
                pageContext.Profile,
                CancellationToken.None
            );
            fileSystem.ApplyPermissions(tempCbz);

            string destination = pageContext.Chapter.UpscaledFullPath!;
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            string? destinationDirectory = Path.GetDirectoryName(destination);
            if (destinationDirectory is not null)
            {
                fileSystem.CreateDirectory(destinationDirectory);
            }

            fileSystem.Move(tempCbz, destination);

            pageContext.Chapter.IsUpscaled = true;
            pageContext.Chapter.UpscalerProfileId = pageContext.Profile.Id;
            await dbContext.SaveChangesAsync();
            await taskProcessor.TaskCompleted(pageContext.Task.Id);
            _ = chapterChangedNotifier.Notify(pageContext.Chapter, true);
        }
        catch
        {
            if (File.Exists(tempCbz))
            {
                File.Delete(tempCbz);
            }

            throw;
        }
    }

    private async Task<PageContext?> ResolvePageContextAsync(int taskId, CancellationToken ct)
    {
        PersistedTask? task = await dbContext.PersistedTasks.FirstOrDefaultAsync(
            t => t.Id == taskId,
            ct
        );
        if (
            task is null
            || task.Status is PersistedTaskStatus.Canceled or PersistedTaskStatus.Completed
        )
        {
            return null;
        }

        if (task.Data is not UpscaleTask upscaleTask)
        {
            return null;
        }

        Chapter? chapter = await dbContext
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(c => c.Id == upscaleTask.ChapterId, ct);
        if (chapter?.UpscaledFullPath is null)
        {
            return null;
        }

        SharedUpscalerProfile? profile = await dbContext.UpscalerProfiles.FirstOrDefaultAsync(
            p => p.Id == upscaleTask.UpscalerProfileId,
            ct
        );
        if (profile is null)
        {
            return null;
        }

        string sourcePath = chapter.NotUpscaledFullPath;
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        List<SpoolPageDescriptor> pages = BuildPageDescriptors(sourcePath, profile);
        if (pages.Count == 0)
        {
            return null;
        }

        return new PageContext(
            task,
            chapter,
            profile,
            pages,
            ComputeIdentity(sourcePath, profile),
            sourcePath
        );
    }

    private static List<SpoolPageDescriptor> BuildPageDescriptors(
        string sourcePath,
        SharedUpscalerProfile profile
    )
    {
        string extension = FormatExtension(profile.CompressionFormat);
        var pages = new List<SpoolPageDescriptor>();
        using ZipArchive archive = ZipFile.OpenRead(sourcePath);
        int index = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!ImageConstants.IsSupportedImageExtension(Path.GetExtension(entry.FullName)))
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(entry.FullName);
            pages.Add(new SpoolPageDescriptor(index, entry.FullName, $"{stem}.{extension}"));
            index++;
        }

        return pages;
    }

    private static string ComputeIdentity(string sourcePath, SharedUpscalerProfile profile)
    {
        FileInfo info = new(sourcePath);
        string material = string.Join(
            '|',
            sourcePath,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            profile.Id,
            (int)profile.CompressionFormat,
            (int)profile.ScalingFactor,
            profile.Quality
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string FormatExtension(SharedCompressionFormat format) =>
        format switch
        {
            SharedCompressionFormat.Avif => "avif",
            SharedCompressionFormat.Png => "png",
            SharedCompressionFormat.Webp => "webp",
            SharedCompressionFormat.Jpg => "jpeg",
            _ => "webp",
        };

    private static UpscalerProfile ToProtoProfile(SharedUpscalerProfile profile) =>
        new()
        {
            Name = profile.Name,
            UpscalerMethod = profile.UpscalerMethod switch
            {
                SharedUpscalerMethod.MangaJaNai => UpscalerMethod.MangaJaNai,
                _ => UpscalerMethod.Unspecified,
            },
            CompressionFormat = profile.CompressionFormat switch
            {
                SharedCompressionFormat.Avif => CompressionFormat.Avif,
                SharedCompressionFormat.Jpg => CompressionFormat.Jpg,
                SharedCompressionFormat.Png => CompressionFormat.Png,
                SharedCompressionFormat.Webp => CompressionFormat.Webp,
                _ => CompressionFormat.Unspecified,
            },
            Quality = profile.Quality,
            ScalingFactor = profile.ScalingFactor switch
            {
                SharedScaleFactor.OneX => ScaleFactor.OneX,
                SharedScaleFactor.TwoX => ScaleFactor.TwoX,
                SharedScaleFactor.ThreeX => ScaleFactor.ThreeX,
                SharedScaleFactor.FourX => ScaleFactor.FourX,
                _ => ScaleFactor.Unspecified,
            },
        };

    private sealed record PageContext(
        PersistedTask Task,
        Chapter Chapter,
        SharedUpscalerProfile Profile,
        List<SpoolPageDescriptor> Pages,
        string Identity,
        string SourcePath
    );
}
