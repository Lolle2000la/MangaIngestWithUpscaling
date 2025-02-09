using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace MangaIngestWithUpscaling.Helpers;

public static class EnumDisplayHelper
{
    public static string GetDisplayName(this Enum enumValue)
    {
        var displayAttribute = enumValue.GetType()
                                        .GetMember(enumValue.ToString())
                                        .First()
                                        .GetCustomAttribute<DisplayAttribute>();

        return displayAttribute?.Name ?? enumValue.ToString();
    }
}
