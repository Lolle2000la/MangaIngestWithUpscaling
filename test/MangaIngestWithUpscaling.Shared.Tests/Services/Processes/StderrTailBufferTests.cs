using System.Reflection;
using MangaIngestWithUpscaling.Shared.Services.Processes;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Processes;

public class StderrTailBufferTests
{
    // The bound is a private implementation detail; read it so the assertions stay exact if it moves.
    private static readonly int MaxLength = (int)
        typeof(StderrTailBuffer)
            .GetField("MaxLength", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

    [Fact]
    [Trait("Category", "Unit")]
    public void Append_KeepsASingleMaximumLengthLineWithinTheBound()
    {
        var buffer = new StderrTailBuffer();

        buffer.Append(new string('x', MaxLength));

        string tail = buffer.GetTail();
        // The newline AppendLine adds counts against the bound, so a line of exactly MaxLength chars
        // is truncated to leave room for it.
        Assert.Equal(MaxLength, tail.Length);
        Assert.EndsWith(Environment.NewLine, tail);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Append_NeverExceedsTheBoundAcrossManyLines()
    {
        var buffer = new StderrTailBuffer();

        for (int i = 0; i < 100; i++)
        {
            buffer.Append(new string((char)('a' + (i % 26)), 1000));
            Assert.True(
                buffer.GetTail().Length <= MaxLength,
                $"The tail exceeded the bound after {i + 1} lines."
            );
        }

        // The newest line must survive the oldest-half trimming.
        buffer.Append("newest");
        Assert.Contains("newest", buffer.GetTail());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Append_DoesNotTruncateOntoALoneHighSurrogate()
    {
        var buffer = new StderrTailBuffer();
        int maxLineLength = MaxLength - Environment.NewLine.Length;

        // Place a surrogate pair so a naive truncation at maxLineLength would cut between its two
        // halves, leaving an unpaired high surrogate.
        string line = new string('a', maxLineLength - 1) + "\uD83D\uDE00" + new string('b', 10);

        buffer.Append(line);

        string tail = buffer.GetTail();
        Assert.True(tail.Length <= MaxLength);
        Assert.False(
            HasUnpairedSurrogate(tail),
            "The tail contains an unpaired surrogate after truncation."
        );
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }
}
