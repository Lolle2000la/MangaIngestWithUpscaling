using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Helpers;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Services.Upscaling;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Services.ImageProcessing;
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
    /// <summary>Upper bound on a single uploaded page, so a malformed or hostile upload cannot fill the spool.</summary>
    private const long MaxPageBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound on a detection result payload, so the lifted gRPC body cap cannot be abused.</summary>
    private const int MaxDetectionResultBytes = 16 * 1024 * 1024;

    public override async Task<PageManifestResponse> GetPageManifest(
        PageManifestRequest request,
        ServerCallContext context
    )
    {
        var resolution = new PageContextResolution();
        PageContext? pageContext = await ResolvePageContextAsync(
            request.TaskId,
            context.CancellationToken,
            resolution
        );
        if (pageContext is null)
        {
            // A terminal task is a genuine not-found; a non-terminal one whose source is momentarily
            // unavailable is a restart, so a flaky mount resumes instead of failing the chapter.
            context.Status = PageContextFailureStatus(resolution);
            return new PageManifestResponse { TaskId = request.TaskId };
        }

        if (string.IsNullOrEmpty(request.EngineIdentity))
        {
            // An empty engine identity would be recorded as "" and silently void the engine-mixing
            // guard, so reject it.
            context.Status = new Status(StatusCode.InvalidArgument, "engine_identity is required");
            return new PageManifestResponse { TaskId = request.TaskId };
        }

        PageStreamSession session = pageStreamSpool.GetOrCreateSession(
            pageContext.Task.Id,
            pageContext.Identity,
            request.EngineIdentity,
            pageContext.Pages.Count
        );

        // Every page was already spooled by a previous run (e.g. assembly failed transiently):
        // finish the chapter instead of asking the worker to produce it again.
        if (pageStreamSpool.IsComplete(session))
        {
            if (!pageStreamSpool.TryBeginAssembly(session, pageContext.Identity))
            {
                if (
                    !string.Equals(session.Identity, pageContext.Identity, StringComparison.Ordinal)
                )
                {
                    // An identity reset raced the finalize; tell the worker to restart rather than
                    // claim the chapter is done when it was never assembled.
                    return await BuildManifestAsync(pageContext, session);
                }

                if (pageStreamSpool.IsFinalized(session))
                {
                    // The session was finalized between the manifest lookup and here while its
                    // identity still matched. Reporting complete would tell the worker the discarded
                    // chapter is done; re-create a fresh session so it re-streams.
                    session = pageStreamSpool.GetOrCreateSession(
                        pageContext.Task.Id,
                        pageContext.Identity,
                        request.EngineIdentity,
                        pageContext.Pages.Count
                    );
                    return await BuildManifestAsync(pageContext, session);
                }

                // Another request is already finalizing; report complete without redoing the work.
                return new PageManifestResponse
                {
                    TaskId = pageContext.Task.Id,
                    TaskIdentity = pageContext.Identity,
                    TaskType = ToProtoTaskType(pageContext.Kind),
                    Complete = true,
                };
            }

            // The winning session: the ResetSpool restart below reassigns `session` to a fresh one,
            // and EndAssembly must clear the flag on the session that actually won TryBeginAssembly.
            PageStreamSession assemblySession = session;

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

                DropPageSpool(session);
            }
            catch (PageStreamRestartException ex)
            {
                // Recoverable: tell the worker to stream the chapter again instead of failing it.
                _logger.LogWarning(
                    ex,
                    "Restarting an already-complete page stream for task {TaskId}.",
                    pageContext.Task.Id
                );
                if (ex.ResetSpool)
                {
                    DropPageSpool(session);
                    // Remove finalized (and deleted) the old session; a manifest built from it would
                    // report every page as completed against a dead spool, so the worker would do
                    // nothing. Re-create a fresh session so the chapter actually re-streams.
                    session = pageStreamSpool.GetOrCreateSession(
                        pageContext.Task.Id,
                        pageContext.Identity,
                        request.EngineIdentity,
                        pageContext.Pages.Count
                    );
                }
                else
                {
                    pageStreamSpool.ForgetMissingPages(session);
                }

                return await BuildManifestAsync(pageContext, session);
            }
            catch (Exception ex)
            {
                if (IsTransientStorageFailure(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Finalizing task {TaskId} hit a storage failure; the worker must retry.",
                        pageContext.Task.Id
                    );
                    // Unavailable rather than Internal: the worker classifies a permanent status as a
                    // terminal failure and would drop the spool this branch exists to preserve.
                    context.Status = new Status(
                        StatusCode.Unavailable,
                        "Finalizing the chapter hit a storage failure; retry"
                    );
                    return new PageManifestResponse
                    {
                        TaskId = pageContext.Task.Id,
                        TaskIdentity = pageContext.Identity,
                        TaskType = ToProtoTaskType(pageContext.Kind),
                    };
                }

                // Do not report success: the worker would return cleanly and the task would linger
                // in Processing. Mark it failed so it is retried or surfaced, and tell the worker.
                _logger.LogError(
                    ex,
                    "Failed to finalize an already-complete page stream for task {TaskId}.",
                    pageContext.Task.Id
                );
                await MarkTaskFailedQuietlyAsync(
                    pageContext.Task.Id,
                    $"Finalizing the chapter failed: {ex.Message}"
                );

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
                pageStreamSpool.EndAssembly(assemblySession);
            }

            return new PageManifestResponse
            {
                TaskId = pageContext.Task.Id,
                TaskIdentity = pageContext.Identity,
                TaskType = ToProtoTaskType(pageContext.Kind),
                Complete = true,
            };
        }

        return await BuildManifestAsync(pageContext, session);
    }

    /// <summary>
    /// Builds the (non-complete) manifest the worker streams against.
    /// </summary>
    private async Task<PageManifestResponse> BuildManifestAsync(
        PageContext pageContext,
        PageStreamSession session
    )
    {
        var response = new PageManifestResponse
        {
            TaskId = pageContext.Task.Id,
            // Use the session's identity, not the freshly resolved one: when an identity change races an
            // in-progress assembly, GetOrCreateSession keeps the old session (so the completed set
            // below is the old identity's), and reporting the new identity with that set would make the
            // worker stream a full pass the commit will reject.
            TaskIdentity = session.Identity,
            TaskType = ToProtoTaskType(pageContext.Kind),
            UpscalerProfile = pageContext.Profile is null
                ? null
                : ToProtoProfile(pageContext.Profile),
            MaxPagePixels = await GetOrComputeMaxPagePixelsAsync(pageContext),
            // The server owns the preprocessing settings; send them so the streamed chapter is
            // preprocessed with the operator's intent rather than the worker's own config.
            PreprocessingJson = JsonSerializer.Serialize(
                ImagePreprocessingOptions.FromConfig(_upscalerConfig)
            ),
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
        var resolution = new PageContextResolution();
        PageContext? pageContext = await ResolvePageContextAsync(
            request.TaskId,
            context.CancellationToken,
            resolution
        );
        if (pageContext is null)
        {
            context.Status = PageContextFailureStatus(resolution);
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
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(pageContext.SourcePath);
            // A malformed archive can contain duplicate entry names; keep the first of each instead of
            // throwing, so one bad entry cannot fail the whole stream.
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                entries.TryAdd(entry.FullName, entry);
            }

            // Bound the request: the receive cap allows millions of ints, and duplicates would otherwise
            // force repeated re-reads.
            int requestedPageCount = pageContext.Pages.Count;
            if (request.PageIndexes.Count > requestedPageCount)
            {
                context.Status = new Status(
                    StatusCode.InvalidArgument,
                    "Too many page indexes requested"
                );
                return;
            }

            bool loggedMissing = false;
            // One buffer reused across the requested pages: a per-page 1 MiB allocation is churn on a
            // large fetch.
            byte[] buffer = new byte[1024 * 1024];
            foreach (int pageIndex in request.PageIndexes.Distinct())
            {
                // Stop early when the client has gone away instead of streaming the rest of the archive.
                if (context.CancellationToken.IsCancellationRequested)
                {
                    context.Status = new Status(
                        StatusCode.Cancelled,
                        "The client cancelled the page fetch."
                    );
                    return;
                }

                if (
                    pageIndex < 0
                    || pageIndex >= requestedPageCount
                    || !byIndex.TryGetValue(pageIndex, out SpoolPageDescriptor? page)
                )
                {
                    context.Status = new Status(
                        StatusCode.InvalidArgument,
                        $"Page index {pageIndex} is not part of task {request.TaskId}'s manifest."
                    );
                    return;
                }

                if (!entries.TryGetValue(page.SourceName, out ZipArchiveEntry? entry))
                {
                    // The manifest promised this entry but the archive no longer has it (a changed or
                    // partially-written source). Returning OK with the page silently skipped would make
                    // the worker classify "the requested page never arrived" as permanent and drop every
                    // already-spooled page; an Unavailable restart preserves the spool and re-resolves
                    // the source, matching how an unreadable archive is treated below.
                    if (!loggedMissing)
                    {
                        _logger.LogWarning(
                            "Requested page {PageIndex} of task {TaskId} is not present in the source archive; asking the worker to restart.",
                            pageIndex,
                            request.TaskId
                        );
                        loggedMissing = true;
                    }
                    context.Status = new Status(
                        StatusCode.Unavailable,
                        "A requested page is missing from the source archive; restart the chapter."
                    );
                    return;
                }

                await using Stream input = entry.Open();
                int chunkNumber = 0;
                int bytesRead;
                long pageBytes = 0;
                while (
                    (
                        bytesRead = await input.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            context.CancellationToken
                        )
                    ) > 0
                )
                {
                    pageBytes += bytesRead;
                    if (pageBytes > MaxPageBytes)
                    {
                        // A decompression bomb: the upload direction is already capped by MaxPageBytes,
                        // so the fetch direction must be too. Fail deterministically rather than
                        // streaming unbounded bytes from one source entry.
                        context.Status = new Status(
                            StatusCode.DataLoss,
                            $"Page {pageIndex} of task {request.TaskId} exceeds the maximum page size."
                        );
                        return;
                    }

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
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // A source archive that cannot be read must not surface as gRPC Unknown (which the worker
            // treats as transient and retries until the soft-failure cap). A corrupt archive is
            // deterministic, so fail it promptly; a locked/unavailable file is a restart.
            _logger.LogWarning(
                ex,
                "Failed to read the source archive for task {TaskId}.",
                request.TaskId
            );
            context.Status =
                ex is InvalidDataException
                    ? new Status(StatusCode.DataLoss, "The source archive is corrupt.")
                    : new Status(
                        StatusCode.Unavailable,
                        "The source archive could not be read; retry the chapter."
                    );
        }
    }

    public override async Task<UploadPageResponse> UploadPage(
        IAsyncStreamReader<UploadPageChunk> requestStream,
        ServerCallContext context
    )
    {
        int taskId = 0;
        int pageIndex = 0;
        string identity = string.Empty;
        string engineIdentity = string.Empty;
        PageContext? pageContext = null;
        PageStreamSession session = null!;
        string? tempPath = null;
        FileStream? pageFile = null;
        long written = 0;
        // In-flight reservations, tracked per generation: an identity reset mid-upload discards the
        // old generation's reservation, so releasing the whole amount against the latest generation
        // would under-count the new one.
        var reservedByGeneration = new Dictionary<long, long>();
        bool sawChunk = false;
        bool sawLast = false;
        bool committed = false;

        try
        {
            try
            {
                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    UploadPageChunk chunk = requestStream.Current;
                    if (
                        sawChunk
                        && (
                            chunk.TaskId != taskId
                            || chunk.PageIndex != pageIndex
                            || !string.Equals(
                                chunk.ContentIdentity,
                                identity,
                                StringComparison.Ordinal
                            )
                            || !string.Equals(
                                chunk.EngineIdentity,
                                engineIdentity,
                                StringComparison.Ordinal
                            )
                        )
                    )
                    {
                        return new UploadPageResponse
                        {
                            Success = false,
                            Message =
                                "The page stream changed task, page, content or engine mid-upload.",
                            TaskId = taskId,
                            PageIndex = pageIndex,
                            Terminal = true,
                        };
                    }

                    if (!sawChunk)
                    {
                        sawChunk = true;
                        taskId = chunk.TaskId;
                        pageIndex = chunk.PageIndex;
                        identity = chunk.ContentIdentity;
                        engineIdentity = chunk.EngineIdentity;

                        var uploadResolution = new PageContextResolution();
                        pageContext = await ResolvePageContextAsync(
                            taskId,
                            context.CancellationToken,
                            uploadResolution
                        );
                        if (pageContext is null)
                        {
                            (string message, bool terminal) = PageContextFailure(uploadResolution);
                            return new UploadPageResponse
                            {
                                Success = false,
                                Message = message,
                                TaskId = taskId,
                                PageIndex = pageIndex,
                                Terminal = terminal,
                            };
                        }

                        if (pageContext.Kind == PageContextKind.Detect)
                        {
                            return new UploadPageResponse
                            {
                                Success = false,
                                Message = "This task is not a page-streamed upscale task.",
                                TaskId = taskId,
                                PageIndex = pageIndex,
                                Terminal = true,
                            };
                        }

                        if (
                            !string.Equals(pageContext.Identity, identity, StringComparison.Ordinal)
                        )
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
                                Message =
                                    $"Page index {pageIndex} is out of range for task {taskId}",
                                TaskId = taskId,
                                PageIndex = pageIndex,
                                Terminal = true,
                            };
                        }

                        PageStreamSession? existing = pageStreamSpool.TryGetSession(taskId);
                        if (existing is null)
                        {
                            // The manifest for this chapter was answered by another replica (or was
                            // never made). Spooling here would never finalize, so reject it loudly and
                            // non-terminally instead of silently looping forever.
                            _logger.LogWarning(
                                "Rejecting an upload for task {TaskId}: no page spool on this server instance. Page streaming requires all of a chapter's RPCs to reach one replica.",
                                taskId
                            );
                            return new UploadPageResponse
                            {
                                Success = false,
                                Message =
                                    "No page spool for this task on this server instance; the chapter must be pinned to one replica",
                                TaskId = taskId,
                                PageIndex = pageIndex,
                                Terminal = false,
                            };
                        }

                        session = existing;
                        // Stream straight to the spool file rather than buffering the whole page in
                        // memory: the gRPC body cap is lifted for uploads, so a large or hostile page
                        // must not drive unbounded allocation.
                        try
                        {
                            pageFile = pageStreamSpool.BeginPageWrite(
                                session,
                                pageIndex,
                                out tempPath
                            );
                        }
                        catch (PageStreamRestartException ex)
                        {
                            // The session was finalized between TryGetSession and here; restart rather
                            // than surface an opaque gRPC Unknown.
                            return new UploadPageResponse
                            {
                                Success = false,
                                Message = ex.Message,
                                TaskId = taskId,
                                PageIndex = pageIndex,
                                Terminal = false,
                            };
                        }
                    }

                    if (!chunk.Chunk.IsEmpty)
                    {
                        long length = chunk.Chunk.Length;
                        if (written + length > MaxPageBytes)
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

                        // Reserve against the per-task budget before writing, so concurrent uploads
                        // cannot each fill temp before any committed-byte check runs.
                        if (
                            !pageStreamSpool.TryReserveInFlight(
                                session,
                                pageIndex,
                                length,
                                out long generation
                            )
                        )
                        {
                            return new UploadPageResponse
                            {
                                Success = false,
                                Message = $"Task {taskId} exceeds the maximum spooled size.",
                                TaskId = taskId,
                                PageIndex = pageIndex,
                                Terminal = true,
                            };
                        }

                        reservedByGeneration[generation] =
                            reservedByGeneration.GetValueOrDefault(generation) + length;
                        written += length;
                        await pageFile!.WriteAsync(chunk.Chunk.Memory, context.CancellationToken);
                    }

                    if (chunk.IsLast)
                    {
                        sawLast = true;
                    }
                }
            }
            finally
            {
                // Drain any remaining chunks before returning: the client is still writing the page
                // and may otherwise fault on a write before it can read our (often non-terminal)
                // response, which it would misclassify as a permanent failure and delete the spool.
                await DrainRequestStreamAsync(requestStream, context.CancellationToken);
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

            // A gracefully-closed but incomplete stream would otherwise be committed as a whole
            // page; require the explicit terminator the worker sends after the last data chunk.
            if (!sawLast)
            {
                return new UploadPageResponse
                {
                    Success = false,
                    Message = "The page upload ended before the final chunk.",
                    TaskId = taskId,
                    PageIndex = pageIndex,
                    Terminal = true,
                };
            }

            if (written == 0)
            {
                return new UploadPageResponse
                {
                    Success = false,
                    Message = "The page upload contained no data.",
                    TaskId = taskId,
                    PageIndex = pageIndex,
                    Terminal = true,
                };
            }

            try
            {
                await pageFile!.DisposeAsync();
                pageFile = null;
                CommitPageResult commitResult = pageStreamSpool.TryCommitPage(
                    session,
                    pageContext!.Identity,
                    engineIdentity,
                    pageIndex,
                    tempPath!,
                    written
                );
                if (commitResult == CommitPageResult.OverBudget)
                {
                    return new UploadPageResponse
                    {
                        Success = false,
                        Message = $"Task {taskId} exceeds the maximum spooled size.",
                        TaskId = taskId,
                        PageIndex = pageIndex,
                        Terminal = true,
                    };
                }

                if (commitResult != CommitPageResult.Committed)
                {
                    // The chapter, profile or engine changed while this page was in flight; drop it
                    // and let the worker restart against the new identity.
                    return new UploadPageResponse
                    {
                        Success = false,
                        Message =
                            commitResult == CommitPageResult.EngineMismatch
                                ? "The upscaling engine changed; restart the chapter"
                                : "The chapter or profile changed; restart the chapter",
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
                // A local storage failure (a full disk, a flaky mount) is transient: restart the
                // chapter so the already-upscaled pages are preserved. The soft-failure cap bounds a
                // persistent failure, so it cannot spin forever. A non-I/O failure is a bug and stays
                // terminal.
                bool terminal = !IsTransientStorageFailure(ex);
                return new UploadPageResponse
                {
                    Success = false,
                    Message = ex.Message,
                    TaskId = taskId,
                    PageIndex = pageIndex,
                    Terminal = terminal,
                };
            }
        }
        finally
        {
            if (pageFile is not null)
            {
                await pageFile.DisposeAsync();
            }

            // Release the bytes this upload reserved; the committed page is counted in TotalBytes.
            // A generation mismatch means an identity reset already discarded the reservation.
            foreach ((long generation, long reserved) in reservedByGeneration)
            {
                pageStreamSpool.ReleaseInFlight(session, generation, reserved);
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
            // False also covers a concurrent identity reset, which the worker must retry rather than
            // treat as "someone else is finalizing".
            if (!string.Equals(session.Identity, pageContext.Identity, StringComparison.Ordinal))
            {
                return new UploadPageResponse
                {
                    Success = false,
                    Message = "The chapter or profile changed; restart the chapter",
                    TaskId = taskId,
                    PageIndex = pageIndex,
                    Terminal = false,
                };
            }

            if (pageStreamSpool.IsFinalized(session))
            {
                // The spool was finalized/removed while this upload was in flight. Reporting success
                // would tell the worker the chapter is assembled when it was discarded; restart.
                return new UploadPageResponse
                {
                    Success = false,
                    Message = "The chapter was finalized while uploading; restart the chapter",
                    TaskId = taskId,
                    PageIndex = pageIndex,
                    Terminal = false,
                };
            }

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
            // Drop the session while it is still marked assembling, so a concurrent manifest cannot
            // replace it between EndAssembly and the detach and have its fresh session detached.
            DropPageSpool(session);
        }
        catch (PageStreamRestartException ex)
        {
            // Recoverable (source changed, a spooled page is missing, or a reset): tell the worker to
            // restart the chapter rather than failing it terminally.
            _logger.LogWarning(ex, "Assembly of task {TaskId} must restart.", taskId);
            if (ex.ResetSpool)
            {
                DropPageSpool(session);
            }
            else
            {
                pageStreamSpool.ForgetMissingPages(session);
            }

            return new UploadPageResponse
            {
                Success = false,
                Message = ex.Message,
                TaskId = taskId,
                PageIndex = pageIndex,
                Terminal = false,
            };
        }
        catch (Exception ex)
        {
            if (IsTransientStorageFailure(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Assembly of task {TaskId} hit a storage failure; keeping the spool for a retry.",
                    taskId
                );
                return new UploadPageResponse
                {
                    Success = false,
                    Message = ex.Message,
                    TaskId = taskId,
                    PageIndex = pageIndex,
                    Terminal = false,
                };
            }

            _logger.LogError(ex, "Failed to assemble upscaled chapter for task {TaskId}", taskId);
            // Mirror the manifest complete-path: mark the task failed rather than relying solely on
            // the worker to report it, which may never arrive if the connection dropped.
            await MarkTaskFailedQuietlyAsync(
                taskId,
                $"Assembling the chapter failed: {ex.Message}"
            );
            return new UploadPageResponse
            {
                Success = false,
                Message = ex.Message,
                TaskId = taskId,
                PageIndex = pageIndex,
                // The task was already terminalised above; the worker must not requeue it.
                Terminal = true,
            };
        }
        finally
        {
            pageStreamSpool.EndAssembly(session);
        }

        return new UploadPageResponse
        {
            Success = true,
            Message = "Chapter upscaled",
            TaskId = taskId,
            PageIndex = pageIndex,
        };
    }

    /// <summary>
    /// Reads and discards the rest of a rejected client-streaming upload. A client-streaming handler
    /// that returns before the client half-closes can make the client's remaining writes fault, so
    /// the client never reads the (non-terminal) response and misclassifies it as a hard failure.
    /// Draining lets the client finish and read the response. Bounded and best-effort.
    /// </summary>
    private static async Task DrainRequestStreamAsync(
        IAsyncStreamReader<UploadPageChunk> requestStream,
        CancellationToken cancellationToken
    )
    {
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(TimeSpan.FromSeconds(30));
        long drained = 0;
        try
        {
            while (await requestStream.MoveNext(drainCts.Token))
            {
                // Read to end-of-stream (not just the terminator) so the client has half-closed
                // before we return, then discard the chunk.
                drained += requestStream.Current.Chunk.Length;
                if (drained > MaxPageBytes)
                {
                    // A rejected upload is not trusted to stay small; stop rather than read forever.
                    break;
                }
            }
        }
        catch (Exception)
        {
            // The client aborted, timed out, or the stream is already closed; nothing left to drain.
        }
    }

    /// <summary>
    /// Drops one specific session (compare-and-remove) and its cached context, offloading the delete.
    /// Used on the finalize paths so a concurrent manifest that already replaced the task's session
    /// is not detached by mistake.
    /// </summary>
    private void DropPageSpool(PageStreamSession session)
    {
        string? directory = pageStreamSpool.Detach(session);
        if (directory is null)
        {
            // A concurrent manifest already replaced this session; its fresh cache entry must stay.
            return;
        }

        pageContextCache.Remove(session.TaskId);
        _ = Task.Run(() => pageStreamSpool.DeleteDirectory(directory));
    }

    public override async Task<UploadDetectionResultResponse> UploadPageDetection(
        UploadPageDetectionRequest request,
        ServerCallContext context
    )
    {
        var resolution = new PageContextResolution();
        PageContext? pageContext = await ResolvePageContextAsync(
            request.TaskId,
            context.CancellationToken,
            resolution
        );
        if (pageContext is null)
        {
            (string message, bool terminal) = PageContextFailure(resolution);
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = message,
                Terminal = terminal,
            };
        }

        if (pageContext.Kind != PageContextKind.Detect)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = "Task is not a page-streamed detection task",
                Terminal = true,
            };
        }

        if (!string.Equals(pageContext.Identity, request.TaskIdentity, StringComparison.Ordinal))
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = "The chapter changed; request a new manifest",
            };
        }

        PageStreamSession? session = pageStreamSpool.TryGetSession(request.TaskId);
        if (session is null)
        {
            _logger.LogWarning(
                "Rejecting a detection result for task {TaskId}: no page spool on this server instance. Page streaming requires all of a chapter's RPCs to reach one replica.",
                request.TaskId
            );
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    "No page spool for this task on this server instance; the chapter must be pinned to one replica",
            };
        }

        if (request.PageIndex < 0 || request.PageIndex >= pageContext.Pages.Count)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"Page index {request.PageIndex} is out of range for task {request.TaskId}",
                Terminal = true,
            };
        }

        // Size-check before allocating the byte array, matching the legacy path.
        if (Encoding.UTF8.GetByteCount(request.ResultJson) > MaxDetectionResultBytes)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} is too large.",
                Terminal = true,
            };
        }

        byte[] resultBytes = Encoding.UTF8.GetBytes(request.ResultJson);

        SplitDetectionResult? detectionResult;
        try
        {
            detectionResult = JsonSerializer.Deserialize(
                request.ResultJson,
                SharedJsonContext.Default.SplitDetectionResult
            );
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Rejected malformed detection JSON for page {PageIndex} of task {TaskId}.",
                request.PageIndex,
                request.TaskId
            );
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} is not valid JSON.",
                Terminal = true,
            };
        }

        if (detectionResult is null)
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} is not valid.",
                Terminal = true,
            };
        }

        // The stored finding is keyed by the image's stem, so a result that names a different image
        // would be misattributed.
        string expectedStem = Path.GetFileNameWithoutExtension(
            pageContext.Pages[request.PageIndex].SourceName
        );
        if (string.IsNullOrEmpty(detectionResult.ImagePath))
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} names a different image.",
                Terminal = true,
            };
        }

        if (
            !string.Equals(
                Path.GetFileNameWithoutExtension(detectionResult.ImagePath),
                expectedStem,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message =
                    $"The detection result for page {request.PageIndex} of task {request.TaskId} names a different image.",
                Terminal = true,
            };
        }

        // Mirror the upscale path's budget reservation: several detection uploads in flight could
        // otherwise each write a full result before any committed-byte check runs.
        if (
            !pageStreamSpool.TryReserveInFlight(
                session,
                request.PageIndex,
                resultBytes.Length,
                out long reservationGeneration
            )
        )
        {
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = $"Task {request.TaskId} exceeds the maximum spooled size.",
                Terminal = true,
            };
        }

        CommitPageResult commitResult;
        string resultTemp = string.Empty;
        try
        {
            FileStream resultFile;
            try
            {
                resultFile = pageStreamSpool.BeginPageWrite(
                    session,
                    request.PageIndex,
                    out resultTemp
                );
            }
            catch (PageStreamRestartException ex)
            {
                // The session was finalized between TryGetSession and here; restart rather than
                // surface an opaque gRPC Unknown.
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = ex.Message,
                    Terminal = false,
                };
            }

            try
            {
                await using (resultFile)
                {
                    await resultFile.WriteAsync(resultBytes, context.CancellationToken);
                }
            }
            catch
            {
                DeleteTempQuietly(resultTemp);
                throw;
            }

            try
            {
                commitResult = pageStreamSpool.TryCommitPage(
                    session,
                    pageContext.Identity,
                    request.EngineIdentity,
                    request.PageIndex,
                    resultTemp,
                    resultBytes.Length
                );
            }
            catch
            {
                DeleteTempQuietly(resultTemp);
                throw;
            }
        }
        finally
        {
            pageStreamSpool.ReleaseInFlight(session, reservationGeneration, resultBytes.Length);
        }

        if (commitResult != CommitPageResult.Committed)
        {
            DeleteTempQuietly(resultTemp);

            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = commitResult switch
                {
                    CommitPageResult.OverBudget =>
                        $"Task {request.TaskId} exceeds the maximum spooled size.",
                    CommitPageResult.EngineMismatch =>
                        "The detector changed; request a new manifest",
                    _ => "The chapter changed; request a new manifest",
                },
                // Over-budget is deterministic; an identity/engine change should restart.
                Terminal = commitResult == CommitPageResult.OverBudget,
            };
        }

        if (!pageStreamSpool.IsComplete(session))
        {
            return new UploadDetectionResultResponse { Success = true, Message = "Result stored" };
        }

        if (!pageStreamSpool.TryBeginAssembly(session, pageContext.Identity))
        {
            // False also covers a concurrent identity reset, which the worker must retry rather than
            // treat as "someone else is finalizing".
            if (!string.Equals(session.Identity, pageContext.Identity, StringComparison.Ordinal))
            {
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = "The chapter or profile changed; restart the chapter",
                    Terminal = false,
                };
            }

            if (pageStreamSpool.IsFinalized(session))
            {
                // The spool was finalized/removed while this upload was in flight. Reporting success
                // would tell the worker the discarded chapter is done; restart instead.
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = "The chapter was finalized while uploading; restart the chapter",
                    Terminal = false,
                };
            }

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
            DropPageSpool(session);

            return new UploadDetectionResultResponse
            {
                Success = true,
                Message = "Detection results processed",
            };
        }
        catch (PageStreamRestartException ex)
        {
            // Recoverable (source changed, a spooled result is missing, or a reset): restart.
            _logger.LogWarning(
                ex,
                "Detection finalize for task {TaskId} must restart.",
                request.TaskId
            );
            if (ex.ResetSpool)
            {
                DropPageSpool(session);
            }
            else
            {
                pageStreamSpool.ForgetMissingPages(session);
            }

            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = ex.Message,
                Terminal = false,
            };
        }
        catch (Exception ex)
        {
            if (IsTransientStorageFailure(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Finalizing detection for task {TaskId} hit a storage failure; keeping the spool for a retry.",
                    request.TaskId
                );
                return new UploadDetectionResultResponse
                {
                    Success = false,
                    Message = ex.Message,
                    Terminal = false,
                };
            }

            _logger.LogError(
                ex,
                "Failed to process page-streamed detection results for task {TaskId}",
                request.TaskId
            );
            await MarkTaskFailedQuietlyAsync(
                request.TaskId,
                $"Finalizing detection failed: {ex.Message}"
            );
            // The task was already terminalised; the worker must not try to requeue it.
            return new UploadDetectionResultResponse
            {
                Success = false,
                Message = ex.Message,
                Terminal = true,
            };
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
        // Read the spooled results under the session gate: a concurrent manifest with a changed
        // identity resets (and deletes) the session directory under the gate, so reading outside it
        // could turn a recoverable restart into an IOException. The files are small JSON. Re-validate
        // the identity under the gate too, so a reset between TryBeginAssembly and here cannot
        // attribute the new identity's results to the old detector version.
        var jsonByPage = new List<string>(pageContext.Pages.Count);
        lock (session.Gate)
        {
            if (
                session.Finalized
                || !string.Equals(session.Identity, pageContext.Identity, StringComparison.Ordinal)
            )
            {
                throw new PageStreamRestartException(
                    $"The spool for task {pageContext.Task.Id} was reset while finalizing; restart the chapter.",
                    resetSpool: true
                );
            }

            foreach (SpoolPageDescriptor page in pageContext.Pages)
            {
                string path = session.PagePath(page.Index);
                if (!File.Exists(path))
                {
                    throw new PageStreamRestartException(
                        $"Spooled detection result {page.Index} for task {pageContext.Task.Id} is missing."
                    );
                }

                jsonByPage.Add(File.ReadAllText(path));
            }
        }

        var results = new List<SplitDetectionResult>(jsonByPage.Count);
        for (int i = 0; i < jsonByPage.Count; i++)
        {
            SplitDetectionResult? result = JsonSerializer.Deserialize(
                jsonByPage[i],
                SharedJsonContext.Default.SplitDetectionResult
            );
            if (result is null)
            {
                throw new InvalidOperationException(
                    $"Spooled detection result {pageContext.Pages[i].Index} for task {pageContext.Task.Id} is invalid."
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

        string destination = pageContext.Chapter.UpscaledFullPath!;
        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (destinationDirectory is not null)
        {
            fileSystem.CreateDirectory(destinationDirectory);
        }

        // Build the CBZ in the destination directory so the final move is a same-filesystem rename
        // (atomic on Unix); deleting the old CBZ before a cross-volume move could lose it if the move
        // failed. AtomicFileReplacement also sweeps what an interrupted run left behind: the spool
        // sweeps only its own roots, so an orphan would otherwise sit in the library for good.
        string tempCbz = Path.Combine(
            destinationDirectory ?? Path.GetTempPath(),
            $".upscaled_{pageContext.Task.Id}_{Guid.NewGuid():N}.tmp"
        );
        using AtomicFileReplacement replacement = AtomicFileReplacement.BeginWithTempPath(
            destination,
            tempCbz,
            _logger
        );

        pageStreamSpool.Assemble(
            session,
            pageContext.Identity,
            pageContext.SourcePath,
            pageContext.Pages,
            replacement.TempPath
        );
        await upscalerJsonHandlingService.WriteUpscalerJsonAsync(
            replacement.TempPath,
            pageContext.Profile!,
            CancellationToken.None
        );
        fileSystem.ApplyPermissions(replacement.TempPath);

        replacement.Commit(fileSystem);

        pageContext.Chapter.IsUpscaled = true;
        pageContext.Chapter.UpscalerProfileId = pageContext.Profile!.Id;
        await dbContext.SaveChangesAsync();
        await taskProcessor.TaskCompleted(pageContext.Task.Id);
        _ = chapterChangedNotifier.Notify(pageContext.Chapter, true);
    }

    /// <summary>
    /// Carries the non-obvious part of a page-context resolution: whether the task itself was missing
    /// or terminal (a genuine not-found) as opposed to the chapter's source being transiently
    /// unavailable (a restart).
    /// </summary>
    private sealed class PageContextResolution
    {
        public bool TaskTerminal { get; set; }

        /// <summary>The task's source archive is corrupt (a deterministic, terminal failure).</summary>
        public bool Corrupt { get; set; }
    }

    /// <summary>gRPC status for a failed page-context resolution.</summary>
    private static Status PageContextFailureStatus(PageContextResolution resolution) =>
        resolution.Corrupt
            ? new Status(StatusCode.DataLoss, "The chapter's source archive is corrupt.")
        : resolution.TaskTerminal
            ? new Status(StatusCode.NotFound, "Task, chapter or profile not found")
        : new Status(
            StatusCode.Unavailable,
            "The chapter's source is not currently available; retry the chapter"
        );

    /// <summary>Message and terminal flag for an upload response to a failed resolution.</summary>
    private static (string Message, bool Terminal) PageContextFailure(
        PageContextResolution resolution
    ) =>
        resolution.Corrupt ? ("The chapter's source archive is corrupt.", true)
        : resolution.TaskTerminal ? ("Task, chapter or profile not found", true)
        : ("The chapter's source is not currently available; restart the chapter", false);

    private async Task<PageContext?> ResolvePageContextAsync(
        int taskId,
        CancellationToken ct,
        PageContextResolution resolution
    )
    {
        PersistedTask? task = await dbContext.PersistedTasks.FirstOrDefaultAsync(
            t => t.Id == taskId,
            ct
        );
        if (
            task is null
            || task.Status
                is PersistedTaskStatus.Canceled
                    or PersistedTaskStatus.Completed
                    or PersistedTaskStatus.Failed
        )
        {
            // The task itself is gone or terminal: a genuine not-found, not a transient source blip.
            resolution.TaskTerminal = true;
            pageContextCache.Remove(taskId);
            return null;
        }

        try
        {
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

                string identity = PageManifestBuilder.ComputeIdentity(sourcePath, profile);
                if (!TryGetCachedPages(task.Id, identity, out List<SpoolPageDescriptor> pages))
                {
                    pages = PageManifestBuilder.BuildPageDescriptors(sourcePath, profile);
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

                // Prefer the task's profile, which is also what the delegation response hands the worker
                // as its engine profile, so the page descriptors/identity and the produced output agree.
                // The chapter's profile is only a fallback if the task's profile was removed.
                SharedUpscalerProfile? profile =
                    await LoadProfileAsync(repairTask.UpscalerProfileId, ct)
                    ?? chapter.UpscalerProfile;
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
                        PageManifestBuilder.ComputeRepairIdentity(
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
                if (differences.Corrupt)
                {
                    // AnalyzePageDifferencesAsync reports a malformed archive as an empty result; the
                    // repair path must classify it as terminal (the handlers map Corrupt to DataLoss)
                    // rather than as a transient restart the worker retries to the soft-failure cap.
                    _logger.LogWarning(
                        "A source archive for repair task {TaskId} is corrupt.",
                        taskId
                    );
                    resolution.Corrupt = true;
                    return null;
                }

                if (differences.ReadFailed)
                {
                    // A transient read failure is not "no differences": restart (return null without
                    // Corrupt) rather than completing the repair as successful.
                    _logger.LogWarning(
                        "Could not read a source archive for repair task {TaskId}; restarting.",
                        taskId
                    );
                    return null;
                }

                if (differences.MissingPages.Count == 0)
                {
                    pageContextCache.Remove(task.Id);
                    return null;
                }

                List<SpoolPageDescriptor> pages = PageManifestBuilder.BuildRepairPageDescriptors(
                    sourcePath,
                    differences.MissingPages,
                    profile
                );
                if (pages.Count == 0)
                {
                    return null;
                }

                string repairIdentity = PageManifestBuilder.ComputeRepairIdentity(
                    sourcePath,
                    chapter.UpscaledFullPath,
                    profile,
                    differences.MissingPages
                );
                pageContextCache.Set(
                    task.Id,
                    new PageContextCache.Entry(
                        repairIdentity,
                        pages,
                        differences.MissingPages.ToList()
                    )
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

                string identity = PageManifestBuilder.ComputeDetectionIdentity(
                    sourcePath,
                    detectTask.DetectorVersion
                );
                if (!TryGetCachedPages(task.Id, identity, out List<SpoolPageDescriptor> pages))
                {
                    pages = PageManifestBuilder.BuildDetectionPageDescriptors(sourcePath);
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
        catch (InvalidDataException ex)
        {
            // A corrupt source archive is deterministic. Surface it as terminal (the handlers map
            // Corrupt to DataLoss) instead of letting it become gRPC Unknown, which the worker treats
            // as transient and retries until the soft-failure cap.
            _logger.LogWarning(ex, "The source archive for task {TaskId} is corrupt.", taskId);
            resolution.Corrupt = true;
            return null;
        }
        catch (IOException ex)
        {
            // A truncated or locked archive that is not a structural corruption: treat it as transient
            // (Unavailable) rather than letting it surface as gRPC Unknown.
            _logger.LogWarning(ex, "Failed to read the source archive for task {TaskId}.", taskId);
            return null;
        }
    }

    /// <summary>Deletes a temp file, ignoring and logging any failure to do so.</summary>
    private void DeleteTempQuietly(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete a page temp file {Temp}.", tempPath);
        }
    }

    /// <summary>
    /// Largest page in the source archive for the worker's inactivity timeout, cached with the page
    /// context so a resume does not re-decode the whole archive on every manifest.
    /// </summary>
    private async Task<long> GetOrComputeMaxPagePixelsAsync(PageContext pageContext)
    {
        if (
            pageContextCache.TryGet(pageContext.Task.Id, out PageContextCache.Entry entry)
            && string.Equals(entry.Identity, pageContext.Identity, StringComparison.Ordinal)
        )
        {
            if (entry.MaxPagePixels > 0)
            {
                return entry.MaxPagePixels;
            }

            long computed = await ComputeMaxPagePixelsAsync(pageContext.SourcePath);
            entry.MaxPagePixels = computed;
            return computed;
        }

        return await ComputeMaxPagePixelsAsync(pageContext.SourcePath);
    }

    /// <summary>Largest page in the source archive, for the worker's inactivity timeout; 0 if unknown.</summary>
    private async Task<long> ComputeMaxPagePixelsAsync(string sourcePath)
    {
        try
        {
            return await imageResizeService.GetMaxPixelCountFromCbzAsync(
                sourcePath,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not compute the max page size for {Source}.", sourcePath);
            return 0;
        }
    }

    /// <summary>
    ///     True when <paramref name="ex" /> — or something it wraps — is a local storage failure: a full
    ///     disk, a flaky mount, a read-only volume. That is the class of error the spool exists to
    ///     absorb, so every page-write, assembly and finalize path treats it the same way: keep the
    ///     spool and let the worker restart the chapter, instead of deleting a chapter's worth of
    ///     already-upscaled pages.
    /// </summary>
    private static bool IsTransientStorageFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks a task failed, swallowing and logging any failure to do so.</summary>
    private async Task MarkTaskFailedQuietlyAsync(int taskId, string errorMessage)
    {
        try
        {
            await taskProcessor.TaskFailed(taskId, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark task {TaskId} as failed.", taskId);
        }
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
    /// the repair service, which merges it into the existing upscaled chapter (and removes extra
    /// pages).
    /// </summary>
    private async Task AssembleRepairedChapterAsync(
        PageContext pageContext,
        PageStreamSession session
    )
    {
        var repairState = taskProcessor.GetRemoteRepairState(pageContext.Task.Id);
        if (repairState is null || string.IsNullOrEmpty(repairState.UpscaledMissingPagesCbzPath))
        {
            // The repair was prepared by the replica that answered the manifest; a finalize landing
            // elsewhere (or after a requeue cleaned the repair files) has no state. Reset the spool so
            // the manifest re-creates it and the worker actually re-streams: a plain restart would
            // leave the completed set intact, so the manifest would report every page complete and the
            // worker would do nothing. The requeue then re-dispatches the task, which re-prepares the
            // repair state via PrepareRepairTaskForRemote.
            throw new PageStreamRestartException(
                $"No prepared repair state for task {pageContext.Task.Id}.",
                resetSpool: true
            );
        }

        pageStreamSpool.AssemblePagesOnly(
            session,
            pageContext.Identity,
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

    private static TaskType ToProtoTaskType(PageContextKind kind) =>
        kind == PageContextKind.Detect ? TaskType.SplitDetection : TaskType.Upscale;

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
