using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PaperTodo;

public static class UiLanguages
{
    public const string System = "system";
    public const string ChineseSimplified = "zh-CN";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";
    public const string Korean = "ko-KR";

#if PAPERTODO_DEFAULT_ENGLISH
    public const string Default = English;
#else
    public const string Default = System;
#endif

    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    public static CultureInfo EffectiveCulture { get; private set; } = SystemCulture;
    public static CultureInfo EffectiveUiCulture { get; private set; } = SystemUiCulture;
    public static bool ShouldApplyThreadCulture { get; private set; }

    public static string Normalize(string? language)
        => language is ChineseSimplified or English or Japanese or Korean ? language : System;

    public static string LoadPersistedPreference()
    {
        foreach (var fileName in new[] { "data.json", "data.backup.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(path),
                    StateJsonReadPolicy.DocumentOptions);
                if (document.RootElement.TryGetProperty("uiLanguage", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return Normalize(value.GetString());
                }
                return Default;
            }
            catch
            {
                // Normal state loading owns corruption reporting; localization is best-effort.
            }
        }
        return Default;
    }

    public static void ConfigureStartupLanguage(string? commandLineLanguage)
    {
        if (TryResolveCommandLineCulture(commandLineLanguage, out var commandCulture))
        {
            EffectiveCulture = commandCulture;
            EffectiveUiCulture = commandCulture;
            ShouldApplyThreadCulture = true;
            return;
        }

        var preference = LoadPersistedPreference();
        ShouldApplyThreadCulture = Normalize(preference) != System;
        EffectiveCulture = ResolveCulture(preference, SystemCulture);
        EffectiveUiCulture = ResolveCulture(preference, SystemUiCulture);
    }

    public static bool TryGetCulture(string? language, out CultureInfo culture)
    {
        var normalized = Normalize(language);
        if (normalized == System)
        {
            culture = null!;
            return false;
        }
        culture = CultureInfo.GetCultureInfo(normalized);
        return true;
    }

    private static CultureInfo ResolveCulture(string? language, CultureInfo systemCulture)
    {
        var normalized = Normalize(language);
        return normalized == System ? systemCulture : CultureInfo.GetCultureInfo(normalized);
    }

    private static bool TryResolveCommandLineCulture(string? language, out CultureInfo culture)
    {
        culture = null!;
        var value = (language ?? "").Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var requested = CultureInfo.GetCultureInfo(value);
            if (requested.TwoLetterISOLanguageName is not ("zh" or "en" or "ja" or "ko"))
                return false;
            culture = requested.IsNeutralCulture
                ? CultureInfo.GetCultureInfo(requested.TwoLetterISOLanguageName switch
                {
                    "zh" => ChineseSimplified,
                    "ja" => Japanese,
                    "ko" => Korean,
                    _ => English
                })
                : requested;
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
