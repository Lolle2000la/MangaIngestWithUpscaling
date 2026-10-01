using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
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

    /// <summary>Upper bound on a single uploaded page, so a malformed or hostile upload cannot fill the spool.</summary>
    private const long MaxPageBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound on a detection result payload, so the lifted gRPC body cap cannot be abused.</summary>
    private const int MaxDetectionResultBytes = 1024 * 1024;

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
        pageContextCache.Sweep(SpoolRetention);
        PageStreamSession session = pageStreamSpool.GetOrCreateSession(
            pageContext.Task.Id,
            pageContext.Identity,
            pageContext.Pages.Count
        );

        // Every page was already spooled by a previous run (e.g. assembly failed transiently):
        // finish the chapter instead of asking the worker to produce it again.
        if (pageStreamSpool.IsComplete(session))
        {
            if (!pageStreamSpool.TryBeginAssembly(session, pageContext.Identity))
            {
                // Another request is already finalizing; report complete without redoing the work.
                return new PageManifestResponse
                {
                    TaskId = pageContext.Task.Id,
                    TaskIdentity = pageContext.Identity,
                    TaskType = ToProtoTaskType(pageContext.Kind),
                    Complete = true,
                };
            }

            try
            {
                if (pageContext.Kind == PageContextKind.Detect)
                {
                    await FinalizeDetectionAsync(pageContext, session);
                }
                else
                {
                    await AssembleUpscaledChapterAsync(pageContext, session);
                }

                pageStreamSpool.Remove(pageContext.Task.Id);
                pageContextCache.Remove(pageContext.Task.Id);
            }
            catch (Exception ex)
            {
                // Do not report success: the worker would return cleanly and the task would linger
                // in Processing. Mark it failed so it is retried or surfaced, and tell the worker.
                _logger.LogError(
                    ex,
                    "Failed to finalize an already-complete page stream for task {TaskId}.",
                    pageContext.Task.Id
                );
                try
                {
                    await taskProcessor.TaskFailed(
                        pageContext.Task.Id,
                        $"Finalizing the chapter failed: {ex.Message}"
                    );
                }
                catch (Exception failEx)
                {
                    _logger.LogWarning(
                        failEx,
                        "Failed to mark task {TaskId} as failed after a finalization error.",
                        pageContext.Task.Id
                    );
                }

                context.Status = new Status(
                    StatusCode.Internal,
                    "Finalizing the already-complete chapter failed; the task was marked failed"
                );
                return new PageManifestResponse
                {
                    TaskId = pageContext.Task.Id,
                    TaskIdentity = pageContext.Identity,
                    TaskType = ToProtoTaskType(pageContext.Kind),
                };
            }
            finally
            {
                pageStreamSpool.EndAssembly(session);
            }

            return new PageManifestResponse
            {
                TaskId = pageContext.Task.Id,
                TaskIdentity = pageContext.Identity,
                TaskType = ToProtoTaskType(pageContext.Kind),
                Complete = true,
            };
        }

        var response = new PageManifestResponse
        {
            TaskId = pageContext.Task.Id,
            TaskIdentity = pageContext.Identity,
            TaskType = ToProtoTaskType(pageContext.Kind),
            UpscalerProfile = pageContext.Profile is null
                ? null
                : ToProtoProfile(pageContext.Profile),
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
        // A malformed archive can contain duplicate entry names; keep the first of each instead of
        // throwing, so one bad entry cannot fail the whole stream.
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            entries.TryAdd(entry.FullName, entry);
        }

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
        PageContext? pageContext = null;
        PageStreamSession session = null!;
        string? tempPath = null;
        FileStream? pageFile = null;
        long written = 0;
        bool sawChunk = false;
        bool committed = false;

        try
        {
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

                if (!sawChunk)
                {
                    sawChunk = true;

                    pageContext = await ResolvePageContextAsync(taskId, CancellationToken.None);
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

                    if (pageIndex < 0 || pageIndex >= pageContext.Pages.Count)
                    {
                        return new UploadPageResponse
                        {
                            Success = false,
                            Message = $"Page index {pageIndex} is out of range for task {taskId}",
                            TaskId = taskId,
                            PageIndex = pageIndex,
                            Terminal = true,
                        };
                    }

                    session = pageStreamSpool.GetOrCreateSession(
                        taskId,
                        pageContext.Identity,
                        pageContext.Pages.Count
                    );
                    // Stream straight to the spool file rather than buffering the whole page in
                    // memory: the gRPC body cap is lifted for uploads, so a large or hostile page
                    // must not drive unbounded allocation.
                    pageFile = pageStreamSpool.BeginPageWrite(session, pageIndex, out tempPath);
                }

                if (!chunk.Chunk.IsEmpty)
                {
                    written += chunk.Chunk.Length;
                    if (written > MaxPageBytes)
                    {
                        return new UploadPageResponse
                        {
                            Success = false,
                            Message =
                                $"Page {pageIndex} of task {taskId} exceeds the maximum page size.",
                            TaskId = taskId,
                            PageIndex = pageIndex,
                            Terminal = true,
                        };
                    }

                    await pageFile!.WriteAsync(chunk.Chunk.Memory, context.CancellationToken);
                }
            }

            if (!sawChunk || taskId == 0)
            {
                return new UploadPageResponse
                {
                    Success = false,
                    Message = "No page data received",
                    Terminal = true,
                };
            }

            try
            {
                await pageFile!.DisposeAsync();
                pageFile = null;
                if (
                    !pageStreamSpool.TryCommitPage(
                        session,
                        pageContext!.Identity,
                        pageIndex,
                        tempPath!
                    )
                )
                {
                    // The chapter or profile changed while this page was in flight; drop it and let
                    // the worker restart against the new identity.
                    return new UploadPageResponse
                    {
                        Success = false,
                        Message = "The chapter or profile changed; restart the chapter",
                        TaskId = taskId,
                        PageIndex = pageIndex,
                    };
                }

                committed = true;
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
        }
        finally
        {
            if (pageFile is not null)
            {
                await pageFile.DisposeAsync();
            }

            if (!committed && tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Failed to delete incomplete page temp file {Temp}.",
                        tempPath
                    );
                }
            }
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

        if (!string.Equals(session.Identity, pageContext!.Identity, StringComparison.Ordinal))
        {
            return new UploadPageResponse
            {
                Success = false,
                Message = "The chapter or profile changed; restart the chapter",
                TaskId = taskId,
                PageIndex = pageIndex,
            };
        }

        if (!pageStreamSpool.TryBeginAssembly(session, pageContext.Identity))
        {
            // Another worker is already assembling the completed chapter.
            return new UploadPageResponse
            {
                Success = true,
                Message = "Another worker is assembling the chapter",
                TaskId = taskId,
                PageIndex = pageIndex,
            };
        }

        try
        {
            await AssembleUpscaledChapterAsync(pageContext!, session);
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
        finally
        {
            pageStreamSpool.EndAssembly(session);
        }

        pageStreamSpool.Remove(taskId);
        pageContextCache.Remove(taskId);
        return new UploadPageResponse
        {
            Success = true,
            Message = "Chapter upscaled",
            TaskId = taskId,
            PageIndex = pageIndex,
        };
    }

    public override async Task<UploadDetectionResultResponse> UploadPageDetection(
        UploadPageDetectionRequest request,
        ServerCallContext context
    )
    {
        PageContext? pageContext = await ResolvePageContextAsync(
            request.TaskId,
            CancellationToken.None
        );
        if (pageContext is null || pageContext.Kind != PageContextKind.Detect)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = "Task is not a page-streamed detection task",
            };
        }

        if (
            !string.IsNullOrEmpty(request.TaskIdentity)
            && !string.Equals(pageContext.Identity, request.TaskIdentity, StringComparison.Ordinal)
        )
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = "The chapter changed; request a new manifest",
            };
        }

        PageStreamSession session = pageStreamSpool.GetOrCreateSession(
            request.TaskId,
            pageContext.Identity,
            pageContext.Pages.Count
        );

        if (request.PageIndex < 0 || request.PageIndex >= pageContext.Pages.Count)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"Page index {request.PageIndex} is out of range for task {request.TaskId}",
            };
        }

        byte[] resultBytes = Encoding.UTF8.GetBytes(request.ResultJson);
        if (resultBytes.Length > MaxDetectionResultBytes)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} is too large.",
            };
        }

        if (
            JsonSerializer.Deserialize(
                request.ResultJson,
                SharedJsonContext.Default.SplitDetectionResult
            )
            is null
        )
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} is not valid.",
            };
        }

        FileStream resultFile = pageStreamSpool.BeginPageWrite(
            session,
            request.PageIndex,
            out string resultTemp
        );
        await using (resultFile)
        {
            await resultFile.WriteAsync(resultBytes, context.CancellationToken);
        }

        if (
            !pageStreamSpool.TryCommitPage(
                session,
                pageContext.Identity,
                request.PageIndex,
                resultTemp
            )
        )
        {
            try
            {
                File.Delete(resultTemp);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Failed to delete a stale detection temp file {Temp}.",
                    resultTemp
                );
            }

            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = "The chapter changed; request a new manifest",
            };
        }

        if (!pageStreamSpool.IsComplete(session))
        {
            return new UploadDetectionResultResponse { Success = true, Message = "Result stored" };
        }

        if (!pageStreamSpool.TryBeginAssembly(session, pageContext.Identity))
        {
            return new UploadDetectionResultResponse
            {
                Success = true,
                Message = "Detection results are already being finalized",
            };
        }

        // Every page reported: finalize the chapter's findings.
        try
        {
            await FinalizeDetectionAsync(pageContext, session);
            pageStreamSpool.Remove(request.TaskId);
            pageContextCache.Remove(request.TaskId);

            return new UploadDetectionResultResponse
            {
                Success = true,
                Message = "Detection results processed",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to process page-streamed detection results for task {TaskId}",
                request.TaskId
            );
            return new UploadDetectionResultResponse { Success = false, Message = ex.Message };
        }
        finally
        {
            pageStreamSpool.EndAssembly(session);
        }
    }

    /// <summary>
    /// Reads the spooled per-page detection results and finalizes the chapter's findings. Used both
    /// when the last page arrives and when an already-complete stream is retried.
    /// </summary>
    private async Task FinalizeDetectionAsync(PageContext pageContext, PageStreamSession session)
    {
        var results = new List<SplitDetectionResult>();
        foreach (SpoolPageDescriptor page in pageContext.Pages)
        {
            string path = session.PagePath(page.Index);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Spooled detection result {page.Index} for task {pageContext.Task.Id} is missing."
                );
            }

            string json = await File.ReadAllTextAsync(path, CancellationToken.None);
            SplitDetectionResult? result = JsonSerializer.Deserialize(
                json,
                SharedJsonContext.Default.SplitDetectionResult
            );
            if (result is null)
            {
                throw new InvalidOperationException(
                    $"Spooled detection result {page.Index} for task {pageContext.Task.Id} is invalid."
                );
            }

            results.Add(result);
        }

        await splitProcessingService.ProcessDetectionResultsAsync(
            pageContext.Chapter.Id,
            results,
            pageContext.DetectorVersion,
            CancellationToken.None
        );
        await taskProcessor.TaskCompleted(pageContext.Task.Id);
    }

    private async Task AssembleUpscaledChapterAsync(
        PageContext pageContext,
        PageStreamSession session
    )
    {
        if (pageContext.Kind == PageContextKind.Repair)
        {
            await AssembleRepairedChapterAsync(pageContext, session);
            return;
        }

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
                pageContext.Profile!,
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
            pageContext.Chapter.UpscalerProfileId = pageContext.Profile!.Id;
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
            pageContextCache.Remove(taskId);
            return null;
        }

        if (task.Data is UpscaleTask upscaleTask)
        {
            Chapter? chapter = await LoadChapterAsync(upscaleTask.ChapterId, ct);
            if (chapter?.UpscaledFullPath is null)
            {
                return null;
            }

            SharedUpscalerProfile? profile = await LoadProfileAsync(
                upscaleTask.UpscalerProfileId,
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

            string identity = ComputeIdentity(sourcePath, profile);
            if (!TryGetCachedPages(task.Id, identity, out List<SpoolPageDescriptor> pages))
            {
                pages = BuildPageDescriptors(sourcePath, profile);
                if (pages.Count == 0)
                {
                    return null;
                }

                pageContextCache.Set(
                    task.Id,
                    new PageContextCache.Entry(identity, pages, Array.Empty<string>())
                );
            }

            return new PageContext(
                task,
                chapter,
                profile,
                pages,
                identity,
                sourcePath,
                Kind: PageContextKind.Upscale
            );
        }

        if (task.Data is RepairUpscaleTask repairTask)
        {
            Chapter? chapter = await LoadChapterAsync(repairTask.ChapterId, ct);
            if (chapter?.UpscaledFullPath is null || !File.Exists(chapter.UpscaledFullPath))
            {
                return null;
            }

            // The worker's engine profile comes from the delegation response
            // (repairTask.UpscalerProfileId); this context's profile drives the page descriptors and
            // identity. They normally agree; if they ever diverge, output naming/scale could drift.
            SharedUpscalerProfile? profile =
                chapter.UpscalerProfile ?? await LoadProfileAsync(repairTask.UpscalerProfileId, ct);
            if (profile is null)
            {
                return null;
            }

            string sourcePath = chapter.NotUpscaledFullPath;
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            // The repair identity covers both files' size/mtime, the profile and the missing set, so
            // re-deriving it from the cached missing set is enough to decide whether the cached
            // context is still valid without re-running the (expensive) archive diff.
            if (
                pageContextCache.TryGet(task.Id, out PageContextCache.Entry cached)
                && cached.MissingPages.Count > 0
                && string.Equals(
                    ComputeRepairIdentity(
                        sourcePath,
                        chapter.UpscaledFullPath,
                        profile,
                        cached.MissingPages
                    ),
                    cached.Identity,
                    StringComparison.Ordinal
                )
            )
            {
                return new PageContext(
                    task,
                    chapter,
                    profile,
                    cached.Pages.ToList(),
                    cached.Identity,
                    sourcePath,
                    Kind: PageContextKind.Repair
                );
            }

            var differences = await metadataHandling.AnalyzePageDifferencesAsync(
                sourcePath,
                chapter.UpscaledFullPath
            );
            if (differences.MissingPages.Count == 0)
            {
                pageContextCache.Remove(task.Id);
                return null;
            }

            List<SpoolPageDescriptor> pages = BuildRepairPageDescriptors(
                sourcePath,
                differences.MissingPages,
                profile
            );
            if (pages.Count == 0)
            {
                return null;
            }

            string repairIdentity = ComputeRepairIdentity(
                sourcePath,
                chapter.UpscaledFullPath,
                profile,
                differences.MissingPages
            );
            pageContextCache.Set(
                task.Id,
                new PageContextCache.Entry(repairIdentity, pages, differences.MissingPages.ToList())
            );

            return new PageContext(
                task,
                chapter,
                profile,
                pages,
                repairIdentity,
                sourcePath,
                Kind: PageContextKind.Repair
            );
        }

        if (task.Data is DetectSplitCandidatesTask detectTask)
        {
            Chapter? chapter = await LoadChapterAsync(detectTask.ChapterId, ct);
            if (chapter is null)
            {
                return null;
            }

            string sourcePath = chapter.NotUpscaledFullPath;
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            string identity = ComputeDetectionIdentity(sourcePath, detectTask.DetectorVersion);
            if (!TryGetCachedPages(task.Id, identity, out List<SpoolPageDescriptor> pages))
            {
                pages = BuildDetectionPageDescriptors(sourcePath);
                if (pages.Count == 0)
                {
                    return null;
                }

                pageContextCache.Set(
                    task.Id,
                    new PageContextCache.Entry(identity, pages, Array.Empty<string>())
                );
            }

            return new PageContext(
                task,
                chapter,
                Profile: null,
                pages,
                identity,
                sourcePath,
                Kind: PageContextKind.Detect,
                DetectorVersion: detectTask.DetectorVersion
            );
        }

        return null;
    }

    private bool TryGetCachedPages(int taskId, string identity, out List<SpoolPageDescriptor> pages)
    {
        if (
            pageContextCache.TryGet(taskId, out PageContextCache.Entry cached)
            && string.Equals(cached.Identity, identity, StringComparison.Ordinal)
        )
        {
            pages = cached.Pages.ToList();
            return true;
        }

        pages = null!;
        return false;
    }

    /// <summary>
    /// Finishes a page-streamed repair: builds a CBZ from the spooled missing pages and hands it to
    /// the task processor, which merges it into the existing upscaled chapter (and removes extra
    /// pages) through the same repair path used for whole-CBZ transfers.
    /// </summary>
    private async Task AssembleRepairedChapterAsync(
        PageContext pageContext,
        PageStreamSession session
    )
    {
        var repairState = taskProcessor.GetRemoteRepairState(pageContext.Task.Id);
        if (repairState is null || string.IsNullOrEmpty(repairState.UpscaledMissingPagesCbzPath))
        {
            throw new InvalidOperationException(
                $"No prepared repair state for task {pageContext.Task.Id}."
            );
        }

        pageStreamSpool.AssemblePagesOnly(
            session,
            pageContext.Pages,
            repairState.UpscaledMissingPagesCbzPath
        );

        // Runs HandleRepairTaskCompletion, which merges the missing pages into the existing
        // upscaled CBZ and marks the task complete.
        await taskProcessor.TaskCompleted(pageContext.Task.Id);
    }

    private async Task<Chapter?> LoadChapterAsync(int chapterId, CancellationToken ct) =>
        await dbContext
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
            .Include(c => c.UpscalerProfile)
            .FirstOrDefaultAsync(c => c.Id == chapterId, ct);

    private async Task<SharedUpscalerProfile?> LoadProfileAsync(
        int profileId,
        CancellationToken ct
    ) => await dbContext.UpscalerProfiles.FirstOrDefaultAsync(p => p.Id == profileId, ct);

    internal static List<SpoolPageDescriptor> BuildPageDescriptors(
        string sourcePath,
        SharedUpscalerProfile profile
    )
    {
        string extension = FormatExtension(profile.CompressionFormat);
        var pages = new List<SpoolPageDescriptor>();
        using ZipArchive archive = ZipFile.OpenRead(sourcePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

            // A malformed archive can repeat an entry name; keep the first so the spool's
            // source-name mapping stays unique.
            if (!seen.Add(entry.FullName))
            {
                continue;
            }

            // Preserve the entry's folder (matching the whole-CBZ worker) so nested pages are not
            // flattened and same-stemmed pages in different folders do not collide.
            pages.Add(
                new SpoolPageDescriptor(
                    index,
                    entry.FullName,
                    ReplaceExtension(entry.FullName, extension)
                )
            );
            index++;
        }

        return pages;
    }

    /// <summary>
    /// Replaces an archive entry's extension while preserving its folder prefix, e.g.
    /// "ch/005.jpg" with "webp" becomes "ch/005.webp".
    /// </summary>
    private static string ReplaceExtension(string entryName, string extension)
    {
        string current = Path.GetExtension(entryName);
        return current.Length == 0
            ? $"{entryName}.{extension}"
            : entryName[..^current.Length] + "." + extension;
    }

    internal static List<SpoolPageDescriptor> BuildRepairPageDescriptors(
        string sourcePath,
        IReadOnlyList<string> missingStems,
        SharedUpscalerProfile profile
    )
    {
        string extension = FormatExtension(profile.CompressionFormat);
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
        {
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

                byStem.TryAdd(Path.GetFileNameWithoutExtension(entry.FullName), entry.FullName);
            }
        }

        var pages = new List<SpoolPageDescriptor>();
        int index = 0;
        foreach (string stem in missingStems)
        {
            if (byStem.TryGetValue(stem, out string? sourceName))
            {
                pages.Add(
                    new SpoolPageDescriptor(
                        index,
                        sourceName,
                        ReplaceExtension(sourceName, extension)
                    )
                );
                index++;
            }
        }

        return pages;
    }

    internal static string ComputeRepairIdentity(
        string sourcePath,
        string upscaledPath,
        SharedUpscalerProfile profile,
        IReadOnlyList<string> missingPages
    )
    {
        FileInfo source = new(sourcePath);
        FileInfo upscaled = new(upscaledPath);
        string material = string.Join(
            '|',
            "repair",
            sourcePath,
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            upscaledPath,
            upscaled.Length,
            upscaled.LastWriteTimeUtc.Ticks,
            profile.Id,
            (int)profile.CompressionFormat,
            (int)profile.ScalingFactor,
            profile.Quality,
            string.Join(',', missingPages.OrderBy(p => p, StringComparer.Ordinal))
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    internal static List<SpoolPageDescriptor> BuildDetectionPageDescriptors(string sourcePath)
    {
        var pages = new List<SpoolPageDescriptor>();
        using ZipArchive archive = ZipFile.OpenRead(sourcePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

            if (!seen.Add(entry.FullName))
            {
                continue;
            }

            pages.Add(new SpoolPageDescriptor(index, entry.FullName, entry.FullName));
            index++;
        }

        return pages;
    }

    internal static string ComputeDetectionIdentity(string sourcePath, int detectorVersion)
    {
        FileInfo info = new(sourcePath);
        string material = string.Join(
            '|',
            "detect",
            sourcePath,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            detectorVersion
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static TaskType ToProtoTaskType(PageContextKind kind) =>
        kind == PageContextKind.Detect ? TaskType.SplitDetection : TaskType.Upscale;

    internal static string ComputeIdentity(string sourcePath, SharedUpscalerProfile profile)
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

    internal static string FormatExtension(SharedCompressionFormat format) =>
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

    private enum PageContextKind
    {
        Upscale,
        Repair,
        Detect,
    }

    private sealed record PageContext(
        PersistedTask Task,
        Chapter Chapter,
        SharedUpscalerProfile? Profile,
        List<SpoolPageDescriptor> Pages,
        string Identity,
        string SourcePath,
        PageContextKind Kind,
        int DetectorVersion = 0
    );
}
