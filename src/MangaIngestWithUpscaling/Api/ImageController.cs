using System.IO.Compression;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Shared.Constants;
using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Api;

/// <summary>
/// Serves individual pages out of chapter archives for the Blazor dialogs that preview a split.
/// The pages expose private library content, so the endpoint is authenticated in every
/// configuration. When OIDC is disabled there is no global fallback authorization policy
/// (see <c>Program.cs</c>), so the attribute here is what protects it; the default scheme is the
/// Identity application cookie, which is registered whether or not OIDC is enabled.
/// </summary>
[ApiController]
[Authorize]
[Route("api/images")]
public class ImageController(ApplicationDbContext dbContext, ILogger<ImageController> logger)
    : ControllerBase
{
    /// <summary>
    /// Upper bound on the decompressed bytes served for one page. It matches the streamed path's
    /// per-page cap (<c>UpscalingDistributionService.Pages.MaxPageBytes</c>) and the local engine's
    /// per-entry cap: without it, an authenticated request naming a highly compressible entry could
    /// pull an unbounded amount of data into server memory.
    /// </summary>
    private const long DefaultMaxPageBytes = ArchiveSizeLimits.MaxEntryBytes;

    /// <summary>
    /// The page-size cap. An internal seam (the app's test assembly is covered by
    /// <c>InternalsVisibleTo</c>, like the engine's internal members) so a test can exercise the
    /// rejection path with a small value; production always uses <see cref="DefaultMaxPageBytes"/>.
    /// </summary>
    internal long MaxPageBytes { get; init; } = DefaultMaxPageBytes;

    [HttpGet("chapter/{chapterId}/file/{fileName}")]
    public async Task<IActionResult> GetChapterImage(int chapterId, string fileName)
    {
        var chapter = await dbContext
            .Chapters.Include(c => c.Manga)
                .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(c => c.Id == chapterId);

        if (chapter == null)
        {
            return NotFound("Chapter not found");
        }

        var libraryPath = chapter.Manga.Library.NotUpscaledLibraryPath;
        var chapterPath = Path.Combine(libraryPath, chapter.RelativePath);

        if (!System.IO.File.Exists(chapterPath))
        {
            return NotFound("Chapter file not found");
        }

        try
        {
            ZipArchive archive = ZipFile.OpenRead(chapterPath);
            bool handedOff = false;
            try
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                );

                if (entry == null)
                {
                    return NotFound("Image not found in archive");
                }

                // Reject before any body is written so the client gets a clear status. The declared
                // length comes from the archive header, so it is a fast rejection, not the only
                // bound: PageSizeLimitedStream counts the bytes that actually pass through.
                if (entry.Length > MaxPageBytes)
                {
                    return StatusCode(
                        StatusCodes.Status413PayloadTooLarge,
                        "Image exceeds the maximum page size."
                    );
                }

                // Stream from the archive instead of buffering the whole entry: the response then
                // holds no more than one copy buffer, and the capped stream aborts if the header lied.
                // Both are disposed when the response completes, not when this action returns.
                Stream entryStream = entry.Open();
                var limited = new PageSizeLimitedStream(entryStream, MaxPageBytes);
                HttpContext.Response.RegisterForDispose(limited);
                HttpContext.Response.RegisterForDispose(archive);
                handedOff = true;
                return File(limited, GetContentType(fileName));
            }
            finally
            {
                if (!handedOff)
                {
                    archive.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to serve image {FileName} from chapter {ChapterId}",
                fileName,
                chapterId
            );
            return StatusCode(500, "Internal server error");
        }
    }

    private string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
    }
}

/// <summary>
/// Read-only passthrough over an archive entry that fails once more than <paramref name="limit"/>
/// bytes have been read. The entry's declared length is checked before the response starts, but a
/// crafted archive can declare a size its stream does not honour, so the bytes that actually pass
/// through are counted as well; the response is aborted rather than allowed to grow without bound.
/// </summary>
internal sealed class PageSizeLimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => ReadAsyncCore(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async ValueTask<int> ReadAsyncCore(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(read);
        return read;
    }

    private void Count(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _read += read;
        if (_read > limit)
        {
            throw new InvalidDataException(
                $"The image exceeds the maximum page size of {limit} bytes."
            );
        }
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
