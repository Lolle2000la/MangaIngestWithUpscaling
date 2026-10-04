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
        // Bound a single line too: one oversized line (e.g. a stack dump) would otherwise push the
        // buffer past MaxLength no matter how the existing content is trimmed.
        if (line.Length > MaxLength)
        {
            line = line[..MaxLength];
        }

        lock (_lock)
        {
            if (_buffer.Length + line.Length + 1 > MaxLength)
            {
                // Drop the oldest half so the tail (most recent diagnostics) is preserved.
                _buffer.Remove(0, _buffer.Length / 2);
            }

            _buffer.AppendLine(line);
        }
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
