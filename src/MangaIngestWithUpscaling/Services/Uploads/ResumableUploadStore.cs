using System.Security.Cryptography;

namespace MangaIngestWithUpscaling.Services.Uploads;

/// <summary>
/// Persists cbz chunks uploaded by a remote worker so an interrupted upload can be resumed
/// without re-sending bytes the server already received. Chunks live in a per-task directory and
/// are written to a temporary file before being atomically moved into place, so a chunk is only
/// ever counted once it is fully on disk.
/// <para>
/// Stored chunks are bound to a <c>contentId</c> supplied by the client. When the client presents a
/// different identity, the old chunks are no longer counted and are replaced on the next write: a
/// task can be re-dispatched with the same id but a new upscaled file, and mixing the two would
/// corrupt the result.
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
    /// identity differs from what is stored, 0 is reported without touching the files; the next
    /// write replaces them. Keeping this path read-only means <c>GetUploadProgress</c> cannot wipe
    /// another in-flight upload.
    /// </summary>
    public async Task<int> GetContiguousChunkCountAsync(int taskId, string? contentId)
    {
        SemaphoreSlim gate = GetTaskLock(taskId);
        await gate.WaitAsync();
        try
        {
            string? stored = ReadIdentity(taskId);
            if (string.IsNullOrEmpty(contentId))
            {
                // A client that declares no identity (older client) must not resume into chunks
                // that belong to an identified upload. Report nothing to resume.
                return stored == null ? CountContiguous(taskId) : 0;
            }

            if (!string.Equals(stored, contentId, StringComparison.Ordinal))
            {
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
        // Hold the task lock so a concurrent delete (identity reset or ReportTaskFailed) cannot
        // remove a chunk mid-assembly and turn a resumable condition into a hard failure.
        SemaphoreSlim gate = GetTaskLock(taskId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            byte[] buffer = new byte[81920];
            for (int chunkNumber = 0; chunkNumber < totalChunks; chunkNumber++)
            {
                await using FileStream chunkStream = File.OpenRead(
                    GetChunkPath(taskId, chunkNumber)
                );
                int bytesRead;
                while ((bytesRead = await chunkStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash?.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(int taskId)
    {
        SemaphoreSlim gate = GetTaskLock(taskId);
        await gate.WaitAsync();
        try
        {
            TryDeleteDirectory(taskId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes task directories whose last write is older than <paramref name="maxAge"/>. Best-effort
    /// and used by the periodic cleanup to bound disk usage from abandoned or cancelled uploads that
    /// never report a failure.
    /// </summary>
    public int SweepStaleUploads(TimeSpan maxAge)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return 0;
        }

        DateTime cutoffUtc = DateTime.UtcNow - maxAge;
        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(_rootDirectory))
        {
            string name = Path.GetFileName(directory);
            if (
                !name.StartsWith("task_", StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan("task_".Length), out int taskId)
            )
            {
                continue;
            }

            SemaphoreSlim gate = GetTaskLock(taskId);
            gate.Wait();
            try
            {
                if (
                    !Directory.Exists(directory)
                    || Directory.GetLastWriteTimeUtc(directory) >= cutoffUtc
                )
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                gate.Release();
            }
        }

        removed += SweepLegacyChunkFiles(cutoffUtc);
        return removed;
    }

    /// <summary>
    /// Removes chunk files left by pre-resumable versions, which wrote
    /// <c>upscaled_{taskId}_{chunk}.chunk</c> next to (not inside) the uploads directory. This store
    /// never reads them, so they would otherwise leak after an upgrade.
    /// </summary>
    private int SweepLegacyChunkFiles(DateTime cutoffUtc)
    {
        DirectoryInfo? parent = Directory.GetParent(_rootDirectory);
        if (parent is null || !parent.Exists)
        {
            return 0;
        }

        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(parent.FullName, "upscaled_*.chunk"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                {
                    continue;
                }

                File.Delete(file);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return removed;
    }

    private async Task EnsureIdentityAsync(int taskId, string? contentId, CancellationToken ct)
    {
        string? stored = ReadIdentity(taskId);

        if (string.IsNullOrEmpty(contentId))
        {
            // A client that doesn't declare an identity (older client) must never build on chunks
            // that belong to an identified upload. Wipe them on the first write; subsequent writes
            // then see no identity file and proceed normally.
            if (stored != null)
            {
                TryDeleteDirectory(taskId);
                Directory.CreateDirectory(GetTaskDirectory(taskId));
            }

            return;
        }

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
