using System.Buffers;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Copies one archive entry's decompressed bytes between streams while enforcing the shared
/// per-entry and per-archive decompression ceilings. Shared by the whole-CBZ writer and the
/// page-streamed spool, so both bound a crafted archive (an entry whose declared size lies about what
/// its stream yields) the same way.
/// </summary>
public static class ArchiveEntryCopier
{
    /// <summary>
    /// Copies an archive entry's decompressed bytes from <paramref name="source"/> into
    /// <paramref name="destination"/>, counting what is actually copied rather than trusting the
    /// zip header: <see cref="System.IO.Compression.ZipArchiveEntry.Length"/> can be unavailable (and a
    /// crafted archive's declared size need not match what its stream yields), so the only sound bound
    /// is the bytes that pass through here. Throws <see cref="InvalidDataException"/> once the entry
    /// exceeds <paramref name="maxEntryBytes"/> or would push the archive past
    /// <paramref name="maxArchiveBytes"/> given <paramref name="archiveBytesAlreadyRead"/>, so an
    /// oversized entry fails loudly instead of filling the disk or process memory. Returns the number
    /// of bytes copied.
    /// </summary>
    public static long CopyWithLimits(
        Stream source,
        Stream destination,
        long maxEntryBytes,
        long maxArchiveBytes,
        long archiveBytesAlreadyRead,
        string entryName
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        long copied = 0;
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;
                if (copied > maxEntryBytes)
                {
                    throw new InvalidDataException(
                        $"Archive entry '{entryName}' exceeds the maximum decompressed entry size of "
                            + $"{maxEntryBytes} bytes."
                    );
                }

                if (archiveBytesAlreadyRead + copied > maxArchiveBytes)
                {
                    throw new InvalidDataException(
                        $"Reading archive entry '{entryName}' would exceed the maximum total "
                            + $"decompressed size of {maxArchiveBytes} bytes for one archive."
                    );
                }

                destination.Write(buffer, 0, read);
            }

            return copied;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
