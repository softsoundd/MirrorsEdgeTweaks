namespace MirrorsEdgeTweaks.Helpers
{
    // Follows how every supported MirrorsEdge.exe picks its language at startup.
    // The game uses its registry Locale value when it recognises it, and otherwise Language= in TdEngine.ini.
    internal static class GameLanguageResolver
    {
        internal const string DefaultLanguageCode = "int";

        // The game compares these prefixes case-sensitively, in this order.
        private static readonly (string Prefix, string LanguageCode)[] LocalePrefixes =
        {
            ("en", "int"),
            ("fr", "fra"),
            ("es", "esn"),
            ("de", "deu"),
            ("it", "ita"),
            ("ru", "rus"),
            ("pl", "pol"),
            ("pt", "por"),
            ("cs", "cze"),
            ("hu", "hun"),
            ("jp", "jpn"),
            ("ko", "kor"),
        };

        // MapRegistryLocale maps each of these values back to its language.
        private static readonly Dictionary<string, string> RegistryLocales = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cze"] = "cs",
            ["deu"] = "de_DE",
            ["int"] = "en_UK",
            ["esn"] = "es_ES",
            ["fra"] = "fr_FR",
            ["ita"] = "it_IT",
            ["hun"] = "hu_HU",
            ["pol"] = "pl_PL",
            ["por"] = "pt_PT",
            ["rus"] = "ru_RU",
            ["kor"] = "ko_KR",
            ["cht"] = "zh_TW",
            ["jpn"] = "jp_JP",
            ["chs"] = "zh_CN",
        };

        // Returns the language code that the game picks for a registry Locale value, such as "int" for "en_UK".
        // Returns null for a value that the game does not recognise.
        internal static string? MapRegistryLocale(string? locale)
        {
            if (string.IsNullOrEmpty(locale))
                return null;

            foreach (var (prefix, languageCode) in LocalePrefixes)
            {
                if (locale.StartsWith(prefix, StringComparison.Ordinal))
                    return languageCode;
            }

            if (locale.Equals("zh_CN", StringComparison.OrdinalIgnoreCase))
                return "chs";
            if (locale.Equals("zh_TW", StringComparison.OrdinalIgnoreCase))
                return "cht";
            return locale.StartsWith("zh", StringComparison.Ordinal) ? "chs" : null;
        }

        // Returns the language code that the game loads.
        // registryBypassed is true while MirrorsEdge.exe has the game language patch, which skips the registry.
        internal static string Resolve(string? registryLocale, string? iniLanguageCode, bool registryBypassed)
        {
            if (!registryBypassed && MapRegistryLocale(registryLocale) is { } registryLanguageCode)
                return registryLanguageCode;

            return string.IsNullOrWhiteSpace(iniLanguageCode)
                ? DefaultLanguageCode
                : iniLanguageCode.Trim().ToLowerInvariant();
        }

        // Returns the language that the registry makes the game load instead of languageCode.
        // Returns null when the registry leaves the language to TdEngine.ini, or names the same language.
        internal static string? FindOverride(string? registryLocale, string languageCode) =>
            MapRegistryLocale(registryLocale) is { } registryLanguageCode
                && !registryLanguageCode.Equals(languageCode.Trim(), StringComparison.OrdinalIgnoreCase)
                ? registryLanguageCode
                : null;

        // Returns a Locale value that the game maps to languageCode.
        // Returns null for an unknown language.
        internal static string? RegistryLocaleFor(string languageCode) =>
            RegistryLocales.TryGetValue(languageCode.Trim(), out string? locale) ? locale : null;
    }
}
