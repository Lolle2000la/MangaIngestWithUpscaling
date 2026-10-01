using System.IO.Compression;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Grpc.Core;
using MangaIngestWithUpscaling.Api.Upscaling;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using SharedCompressionFormat = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.CompressionFormat;
using SharedScaleFactor = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.ScaleFactor;
using UpscalerProfile = MangaIngestWithUpscaling.Shared.Data.LibraryManagement.UpscalerProfile;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Background;

/// <summary>
/// Drives the real <see cref="PageStreamClient"/> against a stubbed gRPC server (a substituted
/// generated client) and a fake local worker, so the fetch/upload/resume wiring is exercised
/// without a real server, database or Python environment.
/// </summary>
public class PageStreamClientTests
{
    private static readonly UpscalerProfile Profile = new()
    {
        Name = "test",
        CompressionFormat = SharedCompressionFormat.Webp,
        ScalingFactor = SharedScaleFactor.TwoX,
        Quality = 80,
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_StreamsAndAssemblesTheChapter()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.Equal(new[] { 0, 1 }, server.RequestedPages);
            Assert.True(File.Exists(destination));

            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(
                new[] { "001.webp", "002.webp", "ComicInfo.xml" },
                zip.Entries.Select(e => e.FullName)
            );
            Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
            Assert.Equal("<ComicInfo/>"u8.ToArray(), ReadEntry(zip, "ComicInfo.xml"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ResumesAtTheFirstMissingPageAfterADrop()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_resume").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            var client = server.CreateClient();

            // First run: the worker drops after the first page, which has already been uploaded.
            var droppingWorker = new FakeWorkerClient { DropAfterPages = 1 };
            var sut = CreateClient(droppingWorker);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                sut.RunAsync(client, 1, Profile, CancellationToken.None)
            );
            Assert.Equal(new[] { 0 }, server.RequestedPages);
            Assert.Contains(0, server.Completed);
            Assert.False(File.Exists(destination));

            // Second run: only the missing page is fetched and the chapter is assembled.
            server.RequestedPages.Clear();
            var recoveringWorker = new FakeWorkerClient();
            var recovering = CreateClient(recoveringWorker);
            await recovering.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.Equal(new[] { 1 }, server.RequestedPages);
            Assert.True(File.Exists(destination));
            using ZipArchive zip = ZipFile.OpenRead(destination);
            Assert.Equal(new byte[] { 3, 2, 1 }, ReadEntry(zip, "001.webp"));
            Assert.Equal(new byte[] { 6, 5, 4 }, ReadEntry(zip, "002.webp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ReturnsImmediatelyWhenTheServerAlreadyCompletedTheChapter()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_done").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination);
            server.Completed.Add(0);
            server.Completed.Add(1);
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            await sut.RunAsync(client, 1, Profile, CancellationToken.None);

            Assert.Empty(server.RequestedPages);
            Assert.Equal(0, worker.ProcessedPages);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ReturnsCleanlyWhenTheCompleteManifestOmitsPages()
    {
        string directory = Directory.CreateTempSubdirectory("page_stream_complete").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { OmitPagesWhenComplete = true };
            server.Completed.UnionWith(server.Pages.Select(p => p.Index));
            var client = server.CreateClient();
            var worker = new FakeWorkerClient();
            var sut = CreateClient(worker);

            // The real server's complete response carries no descriptors; the client must return
            // before the "no pages" guard, not throw.
            await sut.RunAsync(client, 1, Profile, TestContext.Current.CancellationToken);

            Assert.Empty(server.RequestedPages);
            Assert.Equal(0, worker.ProcessedPages);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunDetectionAsync_ReturnsCleanlyWhenTheCompleteManifestOmitsPages()
    {
        string directory = Directory.CreateTempSubdirectory("page_detect_complete").FullName;
        try
        {
            string source = CreateSourceCbz(directory);
            string destination = Path.Combine(directory, "out.cbz");
            var server = new FakePageServer(source, destination) { OmitPagesWhenComplete = true };
            server.Completed.UnionWith(server.Pages.Select(p => p.Index));
            var client = server.CreateClient();
            var sut = CreateClient(new FakeWorkerClient());

            await sut.RunDetectionAsync(client, 1, TestContext.Current.CancellationToken);

            Assert.Empty(server.RequestedPages);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static PageStreamClient CreateClient(FakeWorkerClient worker) =>
        new(
            worker,
            Substitute.For<IServiceScopeFactory>(),
            Options.Create(new UpscalerConfig()),
            Substitute.For<ILogger<PageStreamClient>>()
        );

    private static string CreateSourceCbz(string directory)
    {
        string path = Path.Combine(directory, "source.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "001.jpg", new byte[] { 1, 2, 3 });
        WriteEntry(zip, "002.jpg", new byte[] { 4, 5, 6 });
        WriteEntry(zip, "ComicInfo.xml", "<ComicInfo/>"u8.ToArray());
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using Stream stream = entry.Open();
        stream.Write(data);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using Stream stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Stubbed gRPC server: manifest, page fetch, and per-page upload with assembly.</summary>
    private sealed class FakePageServer
    {
        private const string Identity = "identity";
        private readonly string _sourcePath;
        private readonly string _destinationPath;

        public FakePageServer(string sourcePath, string destinationPath)
        {
            _sourcePath = sourcePath;
            _destinationPath = destinationPath;
            using ZipArchive zip = ZipFile.OpenRead(sourcePath);
            int index = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
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
                Pages.Add(
                    new PageDescriptor
                    {
                        Index = index,
                        SourceName = entry.FullName,
                        OutputName = $"{stem}.webp",
                    }
                );
                index++;
            }
        }

        public List<PageDescriptor> Pages { get; } = new();
        public HashSet<int> Completed { get; } = new();
        public List<int> RequestedPages { get; } = new();
        public Dictionary<int, byte[]> Uploaded { get; } = new();
        public bool OmitPagesWhenComplete { get; set; }

        public UpscalingService.UpscalingServiceClient CreateClient()
        {
            var client = Substitute.For<UpscalingService.UpscalingServiceClient>();
            client
                .GetPageManifestAsync(
                    Arg.Any<PageManifestRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ => Unary(BuildManifest()));
            client
                .GetPages(
                    Arg.Any<GetPagesRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(ci => ServerStream(GetChunks(ci.Arg<GetPagesRequest>())));
            client
                .UploadPage(Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
                .Returns(_ => ClientStream());
            client
                .KeepAliveAsync(
                    Arg.Any<KeepAliveRequest>(),
                    Arg.Any<Metadata>(),
                    Arg.Any<DateTime?>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ => Unary(new KeepAliveResponse { IsAlive = true }));
            return client;
        }

        private PageManifestResponse BuildManifest()
        {
            var response = new PageManifestResponse
            {
                TaskId = 1,
                TaskIdentity = Identity,
                TaskType = TaskType.Upscale,
                Complete = Completed.Count >= Pages.Count,
            };
            // The real server omits the descriptors when it reports the chapter as already
            // complete; allow tests to reproduce that shape.
            if (!(OmitPagesWhenComplete && response.Complete))
            {
                response.Pages.AddRange(Pages);
            }
            response.CompletedPages.AddRange(Completed);
            return response;
        }

        private IEnumerable<PageChunk> GetChunks(GetPagesRequest request)
        {
            using ZipArchive zip = ZipFile.OpenRead(_sourcePath);
            Dictionary<string, ZipArchiveEntry> entries = zip.Entries.ToDictionary(e => e.FullName);
            foreach (int index in request.PageIndexes)
            {
                RequestedPages.Add(index);
                PageDescriptor page = Pages.First(p => p.Index == index);
                using Stream stream = entries[page.SourceName].Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();

                int chunkNumber = 0;
                for (int offset = 0; offset < bytes.Length; offset += 4096)
                {
                    int length = Math.Min(4096, bytes.Length - offset);
                    yield return new PageChunk
                    {
                        TaskId = 1,
                        PageIndex = index,
                        ChunkNumber = chunkNumber++,
                        Chunk = ByteString.CopyFrom(bytes, offset, length),
                    };
                }

                yield return new PageChunk
                {
                    TaskId = 1,
                    PageIndex = index,
                    ChunkNumber = chunkNumber,
                    IsLast = true,
                };
            }
        }

        private AsyncClientStreamingCall<UploadPageChunk, UploadPageResponse> ClientStream()
        {
            var writer = new FakeClientStreamWriter<UploadPageChunk>(chunks =>
            {
                if (chunks.Count == 0)
                {
                    return;
                }

                int pageIndex = chunks[0].PageIndex;
                Uploaded[pageIndex] = chunks
                    .Where(c => !c.Chunk.IsEmpty)
                    .SelectMany(c => c.Chunk.ToByteArray())
                    .ToArray();
                Completed.Add(pageIndex);
                if (Completed.Count >= Pages.Count)
                {
                    Assemble();
                }
            });

            return new AsyncClientStreamingCall<UploadPageChunk, UploadPageResponse>(
                writer,
                Task.FromResult(new UploadPageResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
        }

        private void Assemble()
        {
            using ZipArchive source = ZipFile.OpenRead(_sourcePath);
            using ZipArchive output = ZipFile.Open(_destinationPath, ZipArchiveMode.Create);
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                PageDescriptor? page = Pages.FirstOrDefault(p => p.SourceName == entry.FullName);
                if (page is not null && Uploaded.TryGetValue(page.Index, out byte[]? bytes))
                {
                    ZipArchiveEntry outputEntry = output.CreateEntry(page.OutputName);
                    using Stream target = outputEntry.Open();
                    target.Write(bytes);
                }
                else
                {
                    ZipArchiveEntry outputEntry = output.CreateEntry(entry.FullName);
                    using Stream input = entry.Open();
                    using Stream target = outputEntry.Open();
                    input.CopyTo(target);
                }
            }
        }

        private static AsyncUnaryCall<T> Unary<T>(T value) =>
            new(
                Task.FromResult(value),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );

        private static AsyncServerStreamingCall<PageChunk> ServerStream(
            IEnumerable<PageChunk> chunks
        ) =>
            new(
                new FakeAsyncStreamReader<PageChunk>(chunks),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
    }

    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _items;

        public FakeAsyncStreamReader(IEnumerable<T> items)
        {
            _items = items.GetEnumerator();
        }

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_items.MoveNext())
            {
                Current = _items.Current;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    private sealed class FakeClientStreamWriter<T> : IClientStreamWriter<T>
    {
        private readonly List<T> _items = new();
        private readonly Action<List<T>> _onComplete;

        public FakeClientStreamWriter(Action<List<T>> onComplete)
        {
            _onComplete = onComplete;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            _items.Add(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            _onComplete(_items);
            return Task.CompletedTask;
        }
    }
}
