using MangaIngestWithUpscaling.Shared.Services.Upscaling;

namespace MangaIngestWithUpscaling.Helpers;

/// <summary>
/// Adapts the shared <see cref="NaturalEntryNameComparer" /> to a keyed comparison, so a caller can
/// sort objects by a string property without extracting the key first. The comparison itself lives in
/// the shared comparer — including its invariant numeric parse and its ordinal tie-break for names
/// that differ only in leading zeros ("1.png" vs "01.png") — so this type carries no ordering logic of
/// its own and the whole-CBZ and streamed paths order pages identically.
/// </summary>
/// <param name="propSelector">Selects the string key to compare.</param>
public sealed class NaturalSortComparer<T>(Func<T, string> propSelector) : IComparer<T>
{
    public int Compare(T? x, T? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        return NaturalEntryNameComparer.Instance.Compare(propSelector(x), propSelector(y));
    }
}
