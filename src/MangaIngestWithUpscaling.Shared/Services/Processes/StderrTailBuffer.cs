using System.Text;

namespace MangaIngestWithUpscaling.Shared.Services.Processes;

/// <summary>
/// A bounded tail buffer for subprocess diagnostics. Keeps the most recent lines up to a fixed
/// length, dropping the oldest half when full so the newest output (the part that explains a
/// failure) survives.
/// </summary>
internal sealed class StderrTailBuffer
{
    private const int MaxLength = 8192;
    private readonly Lock _lock = new();
    private readonly StringBuilder _buffer = new();

    public void Append(string line)
    {
        int newlineLength = Environment.NewLine.Length;
        // Leave room for the newline AppendLine adds, so the final length never exceeds MaxLength.
        int maxLineLength = MaxLength - newlineLength;

        lock (_lock)
        {
            // Bound a single line too: one oversized line (e.g. a stack dump) would otherwise push the
            // buffer past MaxLength no matter how the existing content is trimmed. Truncating here
            // (rather than after trimming) keeps the bound exact even when the buffer is empty.
            if (line.Length > maxLineLength)
            {
                line = TruncateWithoutSplittingSurrogatePair(line, maxLineLength);
            }

            // Drop the oldest half until the newest line fits, so the tail (most recent diagnostics)
            // is preserved while the total stays within MaxLength.
            while (_buffer.Length + line.Length + newlineLength > MaxLength && _buffer.Length > 0)
            {
                _buffer.Remove(0, Math.Max(1, _buffer.Length / 2));
            }

            _buffer.AppendLine(line);
        }
    }

    /// <summary>
    /// Truncates <paramref name="line" /> to at most <paramref name="maxLength" /> chars without
    /// ending on an unpaired high surrogate, which would render as a replacement character.
    /// </summary>
    private static string TruncateWithoutSplittingSurrogatePair(string line, int maxLength)
    {
        int length = maxLength;
        if (length > 0 && char.IsHighSurrogate(line[length - 1]))
        {
            // Back off one char so the high surrogate keeps its low surrogate (which is the next
            // char and would otherwise be dropped).
            length--;
        }

        return line[..length];
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    public string GetTail()
    {
        lock (_lock)
        {
            return _buffer.ToString();
        }
    }
}
