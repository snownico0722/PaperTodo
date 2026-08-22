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

    private const int InitialPreferenceReadBufferBytes = 4 * 1024;
    private const int MaximumPreferenceReadBufferBytes = 1024 * 1024;

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
            if (!File.Exists(path))
            {
                continue;
            }

            if (TryReadPersistedPreference(path, out var preference))
            {
                return preference;
            }
        }

        return Default;
    }

    private static bool TryReadPersistedPreference(string path, out string preference)
    {
        preference = Default;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var buffer = new byte[InitialPreferenceReadBufferBytes];
            var bufferedBytes = 0;
            var expectingLanguageValue = false;
            var readerState = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            while (true)
            {
                if (bufferedBytes == buffer.Length)
                {
                    if (buffer.Length >= MaximumPreferenceReadBufferBytes)
                    {
                        return false;
                    }

                    Array.Resize(
                        ref buffer,
                        Math.Min(buffer.Length * 2, MaximumPreferenceReadBufferBytes));
                }

                var bytesRead = stream.Read(
                    buffer,
                    bufferedBytes,
                    buffer.Length - bufferedBytes);
                var totalBytes = bufferedBytes + bytesRead;
                var isFinalBlock = bytesRead == 0;
                var reader = new Utf8JsonReader(
                    new ReadOnlySpan<byte>(buffer, 0, totalBytes),
                    isFinalBlock,
                    readerState);

                while (reader.Read())
                {
                    if (expectingLanguageValue)
                    {
                        preference = reader.TokenType == JsonTokenType.String
                            ? Normalize(reader.GetString())
                            : Default;
                        return true;
                    }

                    if (reader.TokenType == JsonTokenType.PropertyName &&
                        reader.CurrentDepth == 1 &&
                        reader.ValueTextEquals("uiLanguage"))
                    {
                        expectingLanguageValue = true;
                    }
                }

                var consumedBytes = checked((int)reader.BytesConsumed);
                readerState = reader.CurrentState;
                var remainingBytes = totalBytes - consumedBytes;
                if (remainingBytes > 0)
                {
                    Buffer.BlockCopy(
                        buffer,
                        consumedBytes,
                        buffer,
                        0,
                        remainingBytes);
                }
                bufferedBytes = remainingBytes;

                if (isFinalBlock)
                {
                    // A valid primary state without this newer property remains authoritative.
                    return true;
                }
            }
        }
        catch
        {
            // Normal state loading owns corruption/recovery reporting; localization is best-effort.
            return false;
        }
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
