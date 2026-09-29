namespace MangaIngestWithUpscaling.Services.Uploads;

/// <summary>
/// Persists cbz chunks uploaded by a remote worker so an interrupted upload can be resumed
/// without re-sending bytes the server already received. Chunks live in a per-task directory and
/// are written to a temporary file before being atomically moved into place, so a chunk is only
/// ever counted once it is fully on disk.
/// </summary>
public class ResumableUploadStore
{
    private const string ChunkExtension = ".chunk";

    private readonly string _rootDirectory;

    public ResumableUploadStore()
        : this(Path.Combine(Path.GetTempPath(), "mangaingestwithupscaling", "uploads")) { }

    public ResumableUploadStore(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    public string GetTaskDirectory(int taskId) => Path.Combine(_rootDirectory, $"task_{taskId}");

    private string GetChunkPath(int taskId, int chunkNumber) =>
        Path.Combine(GetTaskDirectory(taskId), $"{chunkNumber}{ChunkExtension}");

    /// <summary>
    /// Writes a chunk, replacing any previously stored chunk with the same number. The write is
    /// idempotent, which lets a resuming upload re-send the last chunk to trigger assembly.
    /// </summary>
    public async Task WriteChunkAsync(
        int taskId,
        int chunkNumber,
        byte[] data,
        CancellationToken cancellationToken
    )
    {
        string taskDirectory = GetTaskDirectory(taskId);
        Directory.CreateDirectory(taskDirectory);

        string chunkPath = GetChunkPath(taskId, chunkNumber);
        // A unique temp name keeps concurrent writers for the same chunk from corrupting each
        // other's temp file; the move is atomic and the bytes are identical, so last-write-wins is
        // safe.
        string temporaryPath = $"{chunkPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken);
        File.Move(temporaryPath, chunkPath, overwrite: true);
    }

    /// <summary>
    /// Number of chunks present starting at 0 without gaps. Chunk N is only counted when every
    /// chunk 0..N is already stored.
    /// </summary>
    public int GetContiguousChunkCount(int taskId)
    {
        if (!Directory.Exists(GetTaskDirectory(taskId)))
        {
            return 0;
        }

        int count = 0;
        while (File.Exists(GetChunkPath(taskId, count)))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Concatenates the stored chunks 0..<paramref name="totalChunks"/>-1 into
    /// <paramref name="destination"/>. Callers must ensure the chunks are present.
    /// </summary>
    public async Task AssembleAsync(
        int taskId,
        int totalChunks,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        for (int chunkNumber = 0; chunkNumber < totalChunks; chunkNumber++)
        {
            await using FileStream chunkStream = File.OpenRead(GetChunkPath(taskId, chunkNumber));
            await chunkStream.CopyToAsync(destination, cancellationToken);
        }
    }

    public void Delete(int taskId)
    {
        string taskDirectory = GetTaskDirectory(taskId);
        if (Directory.Exists(taskDirectory))
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }
}
