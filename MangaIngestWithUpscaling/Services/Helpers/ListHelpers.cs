namespace MangaIngestWithUpscaling.Services.Helpers;

public static class ListHelpers
{
    public static void AddOrReplace<T>(this List<T> list, T item, Predicate<T> predicate)
    {
        var index = list.FindIndex(predicate);
        if (index != -1)
        {
            list[index] = item;
        }
        else
        {
            list.Add(item);
        }
    }
}
