namespace GHelper.Linux.I18n;

/// <summary>
/// Internationalization label manager.
/// Loads translations from language dictionaries, supports runtime switching,
/// auto-detects system locale, and persists user preference.
/// </summary>
public static class Labels
{
    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _english = new();

    /// <summary>Fired after language changes - all windows should re-apply labels.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Current language code (e.g. "en", "ru", "zh-cn").</summary>
    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>
    /// Available languages: code → loader function.
    /// Each loader returns a fresh dictionary of translations.
    /// Missing keys fall back to English.
    /// </summary>
    public static readonly Dictionary<string, Func<Dictionary<string, string>>> LanguageLoaders = new()
    {
        { "en",    () => Languages.English.Translations },
        { "ar",    () => Languages.Arabic.Translations },
        { "cs",    () => Languages.Czech.Translations },
        { "da",    () => Languages.Danish.Translations },
        { "de",    () => Languages.German.Translations },
        { "el",    () => Languages.Greek.Translations },
        { "es",    () => Languages.Spanish.Translations },
        { "fi",    () => Languages.Finnish.Translations },
        { "fr",    () => Languages.French.Translations },
        { "hu",    () => Languages.Hungarian.Translations },
        { "id",    () => Languages.Indonesian.Translations },
        { "it",    () => Languages.Italian.Translations },
        { "ja",    () => Languages.Japanese.Translations },
        { "mk",    () => Languages.Macedonian.Translations },
        { "ko",    () => Languages.Korean.Translations },
        { "nl",    () => Languages.Dutch.Translations },
        { "nb",    () => Languages.Norwegian.Translations },
        { "pl",    () => Languages.Polish.Translations },
        { "pt-br", () => Languages.PortugueseBR.Translations },
        { "ro",    () => Languages.Romanian.Translations },
        { "ru",    () => Languages.Russian.Translations },
        { "sr",    () => Languages.Serbian.Translations },
        { "sv",    () => Languages.Swedish.Translations },
        { "th",    () => Languages.Thai.Translations },
        { "tr",    () => Languages.Turkish.Translations },
        { "uk",    () => Languages.Ukrainian.Translations },
        { "vi",    () => Languages.Vietnamese.Translations },
        { "zh-cn", () => Languages.ChineseSimplified.Translations },
        { "zh-tw", () => Languages.ChineseTraditional.Translations },
        { "bn",    () => Languages.Bengali.Translations },
        { "fil",   () => Languages.Filipino.Translations },
        { "hi",    () => Languages.Hindi.Translations },
        { "lt",    () => Languages.Lithuanian.Translations },
        { "lv",    () => Languages.Latvian.Translations },
        { "ms",    () => Languages.Malay.Translations },
        { "ne",    () => Languages.Nepali.Translations },
        { "sk",    () => Languages.Slovak.Translations },
        { "sl",    () => Languages.Slovenian.Translations },
    };

    /// <summary>
    /// Display names for each language (in their native script).
    /// Order matches dropdown display order.
    /// </summary>
    public static readonly (string Code, string NativeName)[] AvailableLanguages =
    {
        ("en",    "English"),
        ("ar",    "\u0627\u0644\u0639\u0631\u0628\u064a\u0629"),           // العربية
        ("cs",    "\u010ce\u0161tina"),               // Čeština
        ("da",    "Dansk"),
        ("de",    "Deutsch"),
        ("el",    "\u0395\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac"),     // Ελληνικά
        ("es",    "Espa\u00f1ol"),                    // Español
        ("fi",    "Suomi"),
        ("fr",    "Fran\u00e7ais"),                   // Français
        ("hu",    "Magyar"),
        ("id",    "Bahasa Indonesia"),
        ("it",    "Italiano"),
        ("ja",    "\u65e5\u672c\u8a9e"),              // 日本語
        ("ko",    "\ud55c\uad6d\uc5b4"),              // 한국어
        ("mk",    "\u041c\u0430\u043a\u0435\u0434\u043e\u043d\u0441\u043a\u0438"), // Македонски
        ("nl",    "Nederlands"),
        ("nb",    "Norsk"),
        ("pl",    "Polski"),
        ("pt-br", "Portugu\u00eas (Brasil)"),         // Português (Brasil)
        ("ro",    "Rom\u00e2n\u0103"),                // Română
        ("ru",    "\u0420\u0443\u0441\u0441\u043a\u0438\u0439"),  // Русский
        ("sr",    "\u0421\u0440\u043f\u0441\u043a\u0438"),    // Српски
        ("sv",    "Svenska"),
        ("th",    "\u0e44\u0e17\u0e22"),              // ไทย
        ("tr",    "T\u00fcrk\u00e7e"),                // Türkçe
        ("uk",    "\u0423\u043a\u0440\u0430\u0457\u043d\u0441\u044c\u043a\u0430"), // Українська
        ("vi",    "Ti\u1ebfng Vi\u1ec7t"),            // Tiếng Việt
        ("zh-cn", "\u7b80\u4f53\u4e2d\u6587"),        // 简体中文
        ("zh-tw", "\u7e41\u9ad4\u4e2d\u6587"),        // 繁體中文
        ("bn",    "\u09ac\u09be\u0982\u09b2\u09be"),  // বাংলা
        ("fil",   "Filipino"),
        ("hi",    "\u0939\u093f\u0928\u094d\u0926\u0940"), // हिन्दी
        ("lt",    "Lietuvi\u0173"),                   // Lietuvių
        ("lv",    "Latvie\u0161u"),                   // Latviešu
        ("ms",    "Bahasa Melayu"),
        ("ne",    "\u0928\u0947\u092a\u093e\u0932\u0940"), // नेपाली
        ("sk",    "Sloven\u010dina"),                 // Slovenčina
        ("sl",    "Sloven\u0161\u010dina"),           // Slovenščina
    };

    /// <summary>
    /// Initialize i18n system. Call once at app startup before any UI is created.
    /// Checks user preference first, then falls back to system locale.
    /// </summary>
    public static void Initialize()
    {
        _english = Languages.English.Translations;

        // User explicitly chose a language → respect it forever
        string? saved = Helpers.AppConfig.GetString("language");
        if (!string.IsNullOrEmpty(saved) && LanguageLoaders.ContainsKey(saved))
        {
            SetLanguageInternal(saved);
            return;
        }

        // Auto-detect from system locale
        string detected = DetectLocale();
        SetLanguageInternal(detected);
    }

    /// <summary>
    /// Get the translated string for a key.
    /// Falls back to English if not found in current language, then to the key itself.
    /// Supports composite format: Labels.Get("key") with string.Format() for {0}, {1}...
    /// </summary>
    public static string Get(string key)
    {
        if (_current.TryGetValue(key, out var val))
            return val;
        if (_english.TryGetValue(key, out var en))
            return en;
        return key;
    }

    /// <summary>
    /// Resolve one label from the process locale without loading or persisting
    /// application preferences. Used by fail-closed early CLI dispatch.
    /// </summary>
    public static string GetForCurrentCultureWithoutInitialization(string key)
    {
        string code = DetectLocale();
        if (LanguageLoaders.TryGetValue(code, out var loader)
            && loader().TryGetValue(key, out string? localized))
            return localized;
        if (Languages.English.Translations.TryGetValue(key, out string? english))
            return english;
        return key;
    }

    /// <summary>
    /// Get a translated format string and apply arguments.
    /// Example: Labels.Format("cpu_fan_info", "65\u00b0C", "2100RPM")
    /// </summary>
    public static string Format(string key, params object[] args)
    {
        return string.Format(Get(key), args);
    }

    /// <summary>
    /// Switch language at runtime. Persists the choice and fires LanguageChanged.
    /// </summary>
    public static void SetLanguage(string code)
    {
        SetLanguageInternal(code);
        Helpers.AppConfig.Set("language", code);
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Reset to auto-detected locale. Clears saved preference and fires LanguageChanged.
    /// </summary>
    public static void ResetToAuto()
    {
        Helpers.AppConfig.Set("language", "");
        string detected = DetectLocale();
        SetLanguageInternal(detected);
        LanguageChanged?.Invoke();
    }

    private static void SetLanguageInternal(string code)
    {
        if (LanguageLoaders.TryGetValue(code, out var loader))
        {
            _current = loader();
            CurrentLanguage = code;
        }
        else
        {
            _current = _english;
            CurrentLanguage = "en";
        }
    }

    /// <summary>
    /// Detect language from system locale environment variables.
    /// Tries LANG, LC_ALL, LC_MESSAGES in order.
    /// Parses "en_US.UTF-8" → "en", "zh_CN.UTF-8" → "zh-cn".
    /// </summary>
    private static string DetectLocale()
    {
        string? lang = Environment.GetEnvironmentVariable("LANG")
                    ?? Environment.GetEnvironmentVariable("LC_ALL")
                    ?? Environment.GetEnvironmentVariable("LC_MESSAGES");

        if (string.IsNullOrEmpty(lang))
            return "en";

        // Remove encoding: "en_US.UTF-8" → "en_US"
        string code = lang.Split('.')[0];

        // Try full match with country: "zh_CN" → "zh-cn", "pt_BR" → "pt-br"
        string full = code.Replace('_', '-').ToLowerInvariant();
        if (LanguageLoaders.ContainsKey(full))
            return full;

        // Try language only: "en_US" → "en"
        string langOnly = code.Split('_')[0].ToLowerInvariant();
        if (LanguageLoaders.ContainsKey(langOnly))
            return langOnly;

        // Special cases: "no" → "nb" (Norwegian Bokmål)
        if (langOnly == "no" && LanguageLoaders.ContainsKey("nb"))
            return "nb";

        // "tl" (Tagalog, ISO 639-1) - "fil" (Filipino): many distros ship the
        // Philippine locale as tl_PH rather than fil_PH, so map it explicitly.
        if (langOnly == "tl" && LanguageLoaders.ContainsKey("fil"))
            return "fil";

        return "en";
    }
}
