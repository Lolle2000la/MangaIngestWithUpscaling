using System.IO.Compression;
using MangaIngestWithUpscaling.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MangaIngestWithUpscaling.Api;

[ApiController]
[Route("api/images")]
public class ImageController(ApplicationDbContext dbContext, ILogger<ImageController> logger)
    : ControllerBase
{
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
            using var archive = ZipFile.OpenRead(chapterPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            );

            if (entry == null)
            {
                return NotFound("Image not found in archive");
            }

            using var entryStream = entry.Open();
            var memoryStream = new MemoryStream();
            await entryStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            var contentType = GetContentType(fileName);
            return File(memoryStream, contentType);
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
