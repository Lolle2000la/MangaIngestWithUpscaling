namespace MangaIngestWithUpscaling.Shared.Helpers;

public static class StringHelpers
{
    public static bool EndsWithAny(this string str, params string[] values)
    {
        foreach (var value in values)
        {
            if (str.EndsWith(value))
            {
                return true;
            }
        }
        return false;
    }
}
