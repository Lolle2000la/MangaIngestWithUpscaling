using System.Security.Cryptography;

namespace MangaIngestWithUpscaling.Services.Uploads;

/// <summary>
/// Persists cbz chunks uploaded by a remote worker so an interrupted upload can be resumed
/// without re-sending bytes the server already received. Chunks live in a per-task directory and
/// are written to a temporary file before being atomically moved into place, so a chunk is only
/// ever counted once it is fully on disk.
/// <para>
/// Stored chunks are bound to a <c>contentId</c> supplied by the client. When the client presents a
/// different identity, the previous chunks are discarded: a task can be re-dispatched with the same
/// id but a new upscaled file, and mixing the two would corrupt the result.
/// </para>
/// </summary>
public class ResumableUploadStore
{
    private const string ChunkExtension = ".chunk";
    private const string IdentityFileName = "identity";
    private const int LockCount = 64;

    // Striped per-task locks serialize identity checks, resets and chunk writes within a process.
    private static readonly SemaphoreSlim[] TaskLocks = Enumerable
        .Range(0, LockCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

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

    private string GetIdentityPath(int taskId) =>
        Path.Combine(GetTaskDirectory(taskId), IdentityFileName);

    private static SemaphoreSlim GetTaskLock(int taskId) => TaskLocks[(uint)taskId % LockCount];

    /// <summary>
    /// Writes a chunk, replacing any previously stored chunk with the same number. The write is
    /// idempotent, which lets a resuming upload re-send the last chunk to trigger assembly.
    /// </summary>
    public async Task WriteChunkAsync(
        int taskId,
        int chunkNumber,
        byte[] data,
        string? contentId,
        CancellationToken cancellationToken
    )
    {
        SemaphoreSlim gate = GetTaskLock(taskId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(GetTaskDirectory(taskId));
            await EnsureIdentityAsync(taskId, contentId, cancellationToken);

            string chunkPath = GetChunkPath(taskId, chunkNumber);
            // A unique temp name keeps concurrent writers for the same chunk from corrupting each
            // other's temp file; the move is atomic and the bytes are identical, so last-write-wins
            // is safe.
            string temporaryPath = $"{chunkPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken);
            File.Move(temporaryPath, chunkPath, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Number of chunks present starting at 0 without gaps, for the given content identity. When the
    /// identity differs from what is stored, the old chunks are discarded and 0 is returned.
    /// </summary>
    public int GetContiguousChunkCount(int taskId, string? contentId)
    {
        SemaphoreSlim gate = GetTaskLock(taskId);
        gate.Wait();
        try
        {
            if (
                !string.IsNullOrEmpty(contentId)
                && !string.Equals(ReadIdentity(taskId), contentId, StringComparison.Ordinal)
            )
            {
                TryDeleteDirectory(taskId);
                return 0;
            }

            return CountContiguous(taskId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Concatenates the stored chunks 0..<paramref name="totalChunks"/>-1 into
    /// <paramref name="destination"/>, feeding each copied byte into <paramref name="hash"/> when
    /// supplied so the caller can verify the result. Callers must ensure the chunks are present.
    /// </summary>
    public async Task AssembleAsync(
        int taskId,
        int totalChunks,
        Stream destination,
        IncrementalHash? hash,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = new byte[81920];
        for (int chunkNumber = 0; chunkNumber < totalChunks; chunkNumber++)
        {
            await using FileStream chunkStream = File.OpenRead(GetChunkPath(taskId, chunkNumber));
            int bytesRead;
            while ((bytesRead = await chunkStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash?.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
    }

    public void Delete(int taskId)
    {
        SemaphoreSlim gate = GetTaskLock(taskId);
        gate.Wait();
        try
        {
            TryDeleteDirectory(taskId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureIdentityAsync(int taskId, string? contentId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(contentId))
        {
            return;
        }

        string? stored = ReadIdentity(taskId);
        if (string.Equals(stored, contentId, StringComparison.Ordinal))
        {
            return;
        }

        // Different (or unknown) content: discard any chunks from the previous upload.
        TryDeleteDirectory(taskId);
        Directory.CreateDirectory(GetTaskDirectory(taskId));
        await File.WriteAllTextAsync(GetIdentityPath(taskId), contentId, ct);
    }

    private string? ReadIdentity(int taskId)
    {
        string path = GetIdentityPath(taskId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private int CountContiguous(int taskId)
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

    private void TryDeleteDirectory(int taskId)
    {
        try
        {
            string directory = GetTaskDirectory(taskId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A concurrent reader/writer can hold a handle (notably on Windows); teardown is
            // best-effort and the assembled-file identity check is the final safeguard.
        }
        catch (UnauthorizedAccessException) { }
    }
}
