namespace GHelper.Linux.I18n.HelpText;

/// <summary>
/// Hover help text per language, by the codes of Labels.LanguageLoaders.
/// A text that a language does not have falls back to English.
/// </summary>
internal static class Help
{
    private static readonly Dictionary<string, (string Toggle, Func<Dictionary<string, string>> Tips)> ByCode = new()
    {
        { "en",    (English.ToggleLabel, () => English.Tips) },
        { "ar",    (Arabic.ToggleLabel, () => Arabic.Tips) },
        { "cs",    (Czech.ToggleLabel, () => Czech.Tips) },
        { "da",    (Danish.ToggleLabel, () => Danish.Tips) },
        { "de",    (German.ToggleLabel, () => German.Tips) },
        { "el",    (Greek.ToggleLabel, () => Greek.Tips) },
        { "es",    (Spanish.ToggleLabel, () => Spanish.Tips) },
        { "fi",    (Finnish.ToggleLabel, () => Finnish.Tips) },
        { "fr",    (French.ToggleLabel, () => French.Tips) },
        { "hu",    (Hungarian.ToggleLabel, () => Hungarian.Tips) },
        { "id",    (Indonesian.ToggleLabel, () => Indonesian.Tips) },
        { "it",    (Italian.ToggleLabel, () => Italian.Tips) },
        { "ja",    (Japanese.ToggleLabel, () => Japanese.Tips) },
        { "mk",    (Macedonian.ToggleLabel, () => Macedonian.Tips) },
        { "ko",    (Korean.ToggleLabel, () => Korean.Tips) },
        { "nl",    (Dutch.ToggleLabel, () => Dutch.Tips) },
        { "nb",    (Norwegian.ToggleLabel, () => Norwegian.Tips) },
        { "pl",    (Polish.ToggleLabel, () => Polish.Tips) },
        { "pt-br", (PortugueseBR.ToggleLabel, () => PortugueseBR.Tips) },
        { "ro",    (Romanian.ToggleLabel, () => Romanian.Tips) },
        { "ru",    (Russian.ToggleLabel, () => Russian.Tips) },
        { "sr",    (Serbian.ToggleLabel, () => Serbian.Tips) },
        { "sv",    (Swedish.ToggleLabel, () => Swedish.Tips) },
        { "th",    (Thai.ToggleLabel, () => Thai.Tips) },
        { "tr",    (Turkish.ToggleLabel, () => Turkish.Tips) },
        { "uk",    (Ukrainian.ToggleLabel, () => Ukrainian.Tips) },
        { "vi",    (Vietnamese.ToggleLabel, () => Vietnamese.Tips) },
        { "zh-cn", (ChineseSimplified.ToggleLabel, () => ChineseSimplified.Tips) },
        { "zh-tw", (ChineseTraditional.ToggleLabel, () => ChineseTraditional.Tips) },
        { "bn",    (Bengali.ToggleLabel, () => Bengali.Tips) },
        { "fil",   (Filipino.ToggleLabel, () => Filipino.Tips) },
        { "hi",    (Hindi.ToggleLabel, () => Hindi.Tips) },
        { "lt",    (Lithuanian.ToggleLabel, () => Lithuanian.Tips) },
        { "lv",    (Latvian.ToggleLabel, () => Latvian.Tips) },
        { "ms",    (Malay.ToggleLabel, () => Malay.Tips) },
        { "ne",    (Nepali.ToggleLabel, () => Nepali.Tips) },
        { "sk",    (Slovak.ToggleLabel, () => Slovak.Tips) },
        { "sl",    (Slovenian.ToggleLabel, () => Slovenian.Tips) },
    };

    internal static string Toggle(string lang)
        => ByCode.TryGetValue(lang, out var l) ? l.Toggle : English.ToggleLabel;

    internal static string? Get(string lang, string key)
        => ByCode.TryGetValue(lang, out var l) && l.Tips().TryGetValue(key, out var text)
            ? text
            : English.Tips.GetValueOrDefault(key);
}
