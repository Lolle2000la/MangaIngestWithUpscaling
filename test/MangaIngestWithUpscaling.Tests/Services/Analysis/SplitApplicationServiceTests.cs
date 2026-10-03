using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.Analysis;
using MangaIngestWithUpscaling.Data.LibraryManagement;
using MangaIngestWithUpscaling.Services.Analysis;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Data.Analysis;
using MangaIngestWithUpscaling.Shared.Data.LibraryManagement;
using MangaIngestWithUpscaling.Shared.Services.Analysis;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using MangaIngestWithUpscaling.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MangaIngestWithUpscaling.Tests.Services.Analysis;

public class SplitApplicationServiceTests : IAsyncDisposable
{
    private readonly TestDatabaseHelper.TestDbContext _testDb;
    private readonly ApplicationDbContext _dbContext;
    private readonly ISplitProcessingCoordinator _coordinator;
    private readonly ISplitApplier _splitApplier;
    private readonly IUpscaler _upscaler;
    private readonly ITaskQueue _taskQueue;
    private readonly ILogger<SplitApplicationService> _logger;
    private readonly SplitApplicationService _service;
    private readonly string _tempDir;

    public SplitApplicationServiceTests()
    {
        _testDb = TestDatabaseHelper.CreateDatabase();
        _dbContext = _testDb.Context;

        _coordinator = Substitute.For<ISplitProcessingCoordinator>();
        _splitApplier = Substitute.For<ISplitApplier>();
        _upscaler = Substitute.For<IUpscaler>();
        _taskQueue = Substitute.For<ITaskQueue>();
        _logger = Substitute.For<ILogger<SplitApplicationService>>();

        _service = CreateService(remoteOnly: false);

        _tempDir = Path.Combine(Path.GetTempPath(), $"split_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    private SplitApplicationService CreateService(bool remoteOnly) =>
        new(
            _dbContext,
            _coordinator,
            _splitApplier,
            _upscaler,
            _taskQueue,
            Options.Create(new UpscalerConfig { RemoteOnly = remoteOnly }),
            _logger,
            Substitute.For<IStringLocalizer<SplitApplicationService>>()
        );

    public async ValueTask DisposeAsync()
    {
        await _testDb.DisposeAsync();

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task ApplySplitsAsync_WhenChapterIsUpscaled_UpscalesSplitPagesOnly()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled"),
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath);

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = true,
            UpscalerProfileId = profile.Id,
            UpscalerProfile = profile,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ with test images (one will be split, one won't)
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "page1.png", "page2.png");

        // Create upscaled CBZ with same pages
        var upscaledCbzPath = chapter.UpscaledFullPath!;
        CreateTestCbz(upscaledCbzPath, "page1.png", "page2.png");

        // Create split finding for page1 only
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page1",
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        _dbContext.StripSplitFindings.Add(finding);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier to create dummy split files
        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                var part2 = Path.Combine(outputDir, "page1_part2.png");
                File.WriteAllText(part1, "dummy part1");
                File.WriteAllText(part2, "dummy part2");
                return new List<string> { part1, part2 };
            });

        // Configure upscaler to create dummy upscaled CBZ
        _upscaler
            .Upscale(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<UpscalerProfile>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                var outputPath = callInfo.ArgAt<string>(1);
                // Create a CBZ with upscaled split pages
                CreateTestCbz(outputPath, "page1_part1.png", "page1_part2.png");
                return Task.CompletedTask;
            });

        // Act
        await _service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert
        // Verify upscaler was called to upscale the split pages
        await _upscaler
            .Received(1)
            .Upscale(Arg.Any<string>(), Arg.Any<string>(), profile, Arg.Any<CancellationToken>());

        // Verify the upscaled CBZ still exists and has been updated
        Assert.True(File.Exists(upscaledCbzPath), "Upscaled CBZ should still exist");

        // Chapter should remain upscaled
        await _dbContext.Entry(chapter).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(chapter.IsUpscaled, "Chapter should still be marked as upscaled");
        Assert.Equal(profile.Id, chapter.UpscalerProfileId);

        // Verify coordinator was notified
        await _coordinator
            .Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySplitsAsync_RemoteOnly_EnqueuesARepairInsteadOfUpscalingInline()
    {
        // Arrange: an upscaled chapter with a split finding (a remote-only server has no ML backend).
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original_remote"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled_remote"),
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath);

        var profile = new UpscalerProfile
        {
            Name = "Test Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = true,
            UpscalerProfileId = profile.Id,
            UpscalerProfile = profile,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "page1.png", "page2.png");

        var upscaledCbzPath = chapter.UpscaledFullPath!;
        CreateTestCbz(upscaledCbzPath, "page1.png", "page2.png");

        _dbContext.StripSplitFindings.Add(
            new StripSplitFinding
            {
                ChapterId = chapter.Id,
                DetectorVersion = 1,
                PageFileName = "page1",
                SplitJson = JsonSerializer.Serialize(
                    new SplitDetectionResult
                    {
                        OriginalHeight = 1000,
                        Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                    }
                ),
            }
        );
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                File.WriteAllText(part1, "dummy part1");
                return new List<string> { part1 };
            });

        var service = CreateService(remoteOnly: true);

        // Act
        await service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert: no local ML backend call; the worker is asked to repair the split chapter (a plain
        // UpscaleTask would be skipped because the chapter is already upscaled).
        await _upscaler
            .DidNotReceiveWithAnyArgs()
            .Upscale(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<UpscalerProfile>(),
                Arg.Any<CancellationToken>()
            );
        await _taskQueue.Received(1).EnqueueAsync(Arg.Any<RepairUpscaleTask>());
        await _coordinator
            .Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySplitsAsync_RemoteOnly_WithInheritedProfile_StillEnqueuesARepair()
    {
        // Arrange: the chapter is upscaled but has no explicit profile FK; the profile is inherited
        // from the library. The repair enqueue must still happen, which requires the query to load
        // Manga.Library.UpscalerProfile (and Manga.UpscalerProfilePreference).
        var profile = new UpscalerProfile
        {
            Name = "Inherited Profile",
            ScalingFactor = ScaleFactor.TwoX,
            CompressionFormat = CompressionFormat.Png,
            Quality = 90,
        };
        _dbContext.UpscalerProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var library = new Library
        {
            Name = "Inherited Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original_inherited"),
            UpscaledLibraryPath = Path.Combine(_tempDir, "upscaled_inherited"),
            UpscaleOnIngest = false,
            UpscalerProfileId = profile.Id,
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);
        Directory.CreateDirectory(library.UpscaledLibraryPath);

        var manga = new Manga
        {
            PrimaryTitle = "Inherited Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = true,
            // No explicit profile: it is inherited from the library.
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "page1.png", "page2.png");
        CreateTestCbz(chapter.UpscaledFullPath!, "page1.png", "page2.png");

        _dbContext.StripSplitFindings.Add(
            new StripSplitFinding
            {
                ChapterId = chapter.Id,
                DetectorVersion = 1,
                PageFileName = "page1",
                SplitJson = JsonSerializer.Serialize(
                    new SplitDetectionResult
                    {
                        OriginalHeight = 1000,
                        Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                    }
                ),
            }
        );
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                File.WriteAllText(part1, "dummy part1");
                return new List<string> { part1 };
            });

        // Drop the tracked graph so the service's own query (with its Includes) must resolve the
        // inherited profile from the database rather than reusing an already-populated navigation.
        _dbContext.ChangeTracker.Clear();

        var service = CreateService(remoteOnly: true);

        // Act
        await service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert: the inherited profile is resolved and a repair is enqueued.
        await _taskQueue
            .Received(1)
            .EnqueueAsync(
                Arg.Is<RepairUpscaleTask>(t =>
                    t.ChapterId == chapter.Id && t.UpscalerProfileId == profile.Id
                )
            );
    }

    [Fact]
    public async Task ApplySplitsAsync_WhenChapterIsNotUpscaled_DoesNotAttemptToDeleteUpscaled()
    {
        // Arrange
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original"),
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = false,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "page1.png");

        // Create split finding
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page1",
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        _dbContext.StripSplitFindings.Add(finding);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier
        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "page1_part1.png");
                var part2 = Path.Combine(outputDir, "page1_part2.png");
                File.WriteAllText(part1, "dummy");
                File.WriteAllText(part2, "dummy");
                return new List<string> { part1, part2 };
            });

        // Act
        await _service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert
        await _dbContext.Entry(chapter).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(chapter.IsUpscaled);

        await _coordinator
            .Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySplitsAsync_MatchesFindingsCaseInsensitively()
    {
        // Arrange - test that filename matching is case-insensitive
        var library = new Library
        {
            Name = "Test Library",
            NotUpscaledLibraryPath = Path.Combine(_tempDir, "original"),
        };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(library.NotUpscaledLibraryPath);

        var manga = new Manga
        {
            PrimaryTitle = "Test Manga",
            LibraryId = library.Id,
            Library = library,
        };
        _dbContext.MangaSeries.Add(manga);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chapter = new Chapter
        {
            FileName = "chapter1.cbz",
            RelativePath = "manga/chapter1.cbz",
            MangaId = manga.Id,
            Manga = manga,
            IsUpscaled = false,
        };
        _dbContext.Chapters.Add(chapter);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Create original CBZ with UPPERCASE filename
        var originalCbzPath = Path.Combine(library.NotUpscaledLibraryPath, chapter.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalCbzPath)!);
        CreateTestCbz(originalCbzPath, "PAGE001.png");

        // Create split finding with lowercase PageFileName
        var finding = new StripSplitFinding
        {
            ChapterId = chapter.Id,
            DetectorVersion = 1,
            PageFileName = "page001", // lowercase
            SplitJson = JsonSerializer.Serialize(
                new SplitDetectionResult
                {
                    OriginalHeight = 1000,
                    Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                }
            ),
        };
        _dbContext.StripSplitFindings.Add(finding);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Configure split applier
        _splitApplier
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            )
            .Returns(callInfo =>
            {
                var outputDir = callInfo.ArgAt<string>(2);
                var part1 = Path.Combine(outputDir, "PAGE001_part1.png");
                var part2 = Path.Combine(outputDir, "PAGE001_part2.png");
                File.WriteAllText(part1, "dummy");
                File.WriteAllText(part2, "dummy");
                return new List<string> { part1, part2 };
            });

        // Act
        await _service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

        // Assert - the split should have been applied despite case mismatch
        _splitApplier
            .Received(1)
            .ApplySplitsToImage(
                Arg.Any<string>(),
                Arg.Any<List<DetectedSplit>>(),
                Arg.Any<string>()
            );

        await _coordinator
            .Received(1)
            .OnSplitsAppliedAsync(chapter.Id, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySplitsAsync_ReplacesTheChapterByRenameSoAReaderKeepsTheArchiveItOpened()
    {
        // Replacing a chapter used to be a File.Move from the apply's scratch space under the system
        // temp directory, which is usually a different mount than the library: that fallback copies into
        // the destination's inode, so a worker streaming pages from the chapter read a half-written
        // archive. Building the replacement next to the chapter makes the swap a rename. /dev/shm is a
        // separate mount from /tmp on Linux, which is the shape the bug needs.
        string libraryRoot = Path.Combine(SharedMemoryRoot, $"split_rename_{Guid.NewGuid():N}");
        Assert.SkipWhen(
            !DirectoryCanLiveOnAnotherFilesystem(libraryRoot),
            NoSecondFilesystemSkipReason
        );

        try
        {
            var library = new Library
            {
                Name = "Rename Library",
                NotUpscaledLibraryPath = Path.Combine(libraryRoot, "original"),
            };
            _dbContext.Libraries.Add(library);
            await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

            Directory.CreateDirectory(library.NotUpscaledLibraryPath);

            var manga = new Manga
            {
                PrimaryTitle = "Rename Manga",
                LibraryId = library.Id,
                Library = library,
            };
            _dbContext.MangaSeries.Add(manga);
            await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

            var chapter = new Chapter
            {
                FileName = "chapter1.cbz",
                RelativePath = "manga/chapter1.cbz",
                MangaId = manga.Id,
                Manga = manga,
                IsUpscaled = false,
            };
            _dbContext.Chapters.Add(chapter);
            await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

            var originalCbzPath = Path.Combine(
                library.NotUpscaledLibraryPath,
                chapter.RelativePath
            );
            CreateTestCbz(originalCbzPath, "page1.png");

            _dbContext.StripSplitFindings.Add(
                new StripSplitFinding
                {
                    ChapterId = chapter.Id,
                    DetectorVersion = 1,
                    PageFileName = "page1",
                    SplitJson = JsonSerializer.Serialize(
                        new SplitDetectionResult
                        {
                            OriginalHeight = 1000,
                            Splits = [new DetectedSplit { YOriginal = 500, Confidence = 0.9 }],
                        }
                    ),
                }
            );
            await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

            _splitApplier
                .ApplySplitsToImage(
                    Arg.Any<string>(),
                    Arg.Any<List<DetectedSplit>>(),
                    Arg.Any<string>()
                )
                .Returns(callInfo =>
                {
                    var outputDir = callInfo.ArgAt<string>(2);
                    var part1 = Path.Combine(outputDir, "page1_part1.png");
                    var part2 = Path.Combine(outputDir, "page1_part2.png");
                    File.WriteAllText(part1, "dummy");
                    File.WriteAllText(part2, "dummy");
                    return new List<string> { part1, part2 };
                });

            // How a worker holds the chapter open while it streams pages out of it.
            using FileStream openedBefore = new(
                originalCbzPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );

            await _service.ApplySplitsAsync(chapter.Id, 1, TestContext.Current.CancellationToken);

            // The handle still sees the archive it opened; a copy into that inode would have rewritten
            // the bytes underneath the reader mid-stream.
            using (var stillOpen = new ZipArchive(openedBefore, ZipArchiveMode.Read))
            {
                Assert.Equal(
                    new[] { "page1.png" },
                    stillOpen.Entries.Select(e => e.FullName).ToArray()
                );
            }

            // The chapter itself did get the split result.
            using var replaced = ZipFile.OpenRead(originalCbzPath);
            Assert.Equal(
                new[] { "page1_part1.png", "page1_part2.png" },
                replaced
                    .Entries.Select(e => e.FullName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray()
            );
        }
        finally
        {
            if (Directory.Exists(libraryRoot))
            {
                Directory.Delete(libraryRoot, true);
            }
        }
    }

    private const string SharedMemoryRoot = "/dev/shm";

    private const string NoSecondFilesystemSkipReason =
        "Needs a library on a filesystem other than the process's temp directory (/dev/shm on Linux) "
        + "to exercise a cross-filesystem replacement.";

    /// <summary>
    ///     True when <paramref name="probePath" /> can be created on a mount distinct from the one
    ///     holding <see cref="Path.GetTempPath" />. Distinct mounts are the point: a rename between them
    ///     fails with EXDEV, which is when File.Move falls back to a copying in-place rewrite.
    /// </summary>
    private static bool DirectoryCanLiveOnAnotherFilesystem(string probePath)
    {
        try
        {
            if (!Directory.Exists(SharedMemoryRoot))
            {
                return false;
            }

            if (
                string.Equals(
                    new DriveInfo(Path.GetTempPath()).Name,
                    new DriveInfo(SharedMemoryRoot).Name,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }

            Directory.CreateDirectory(probePath);
            string probe = Path.Combine(probePath, "probe");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            // No separate mount (or not writable): the test is skipped rather than reported as a pass.
            return false;
        }
    }

    private void CreateTestCbz(string path, params string[] imageNames)
    {
        var tempExtractDir = Path.Combine(_tempDir, $"extract_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            // Create dummy image files
            foreach (var imageName in imageNames)
            {
                var imagePath = Path.Combine(tempExtractDir, imageName);
                File.WriteAllText(imagePath, $"dummy image content for {imageName}");
            }

            // Create CBZ (which is just a ZIP file)
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            ZipFile.CreateFromDirectory(tempExtractDir, path);
        }
        finally
        {
            if (Directory.Exists(tempExtractDir))
            {
                Directory.Delete(tempExtractDir, true);
            }
        }
    }
}
