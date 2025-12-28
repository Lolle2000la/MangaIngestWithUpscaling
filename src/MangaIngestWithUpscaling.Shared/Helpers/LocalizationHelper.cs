using System.Globalization;
using Humanizer;

namespace MangaIngestWithUpscaling.Shared.Helpers;

public static class LocalizationHelper
{
    public static string ToLocalizedQuantity(
        this string word,
        int quantity,
        CultureInfo? culture = null
    )
    {
        culture ??= CultureInfo.CurrentUICulture;

        // Humanizer defaults to English pluralization rules (adding 's') if it doesn't have specific rules for the language
        // or if the word is not in its vocabulary. For languages like Japanese that don't use plural suffixes,
        // we want to avoid this default behavior.
        if (culture.TwoLetterISOLanguageName == "ja")
        {
            return $"{quantity} {word}";
        }

        return word.ToQuantity(quantity, formatProvider: culture);
    }
}
