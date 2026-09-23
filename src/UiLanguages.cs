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

    // Capture the real process/system cultures before PaperTodo applies any preference.
    // Language switching is restart-based: resolve one effective culture for this process
    // and keep resource lookup independent from async/Dispatcher ExecutionContext culture flow.
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static readonly string StartupPreference = LoadPersistedPreferenceCore();

    // Intentionally fixed for the process lifetime; a settings change takes effect after restart.
    // EffectiveCulture owns user-visible number/date formatting and parsing; EffectiveUiCulture
    // owns resource lookup plus WPF language/shaping. Persistence and protocols choose explicitly.
    public static CultureInfo EffectiveCulture { get; } =
        ResolveCulture(StartupPreference, SystemCulture);

    public static CultureInfo EffectiveUiCulture { get; } =
        ResolveCulture(StartupPreference, SystemUiCulture);

    public static string Normalize(string? language)
    {
        return language is ChineseSimplified or English or Japanese or Korean
            ? language
            : System;
    }

    public static string LoadPersistedPreference() => StartupPreference;

    private static string LoadPersistedPreferenceCore()
    {
        foreach (var fileName in new[] { "data.json", "data.backup.json" })
        {
            var path = Path.Combine(AppPaths.ExecutableDirectory, fileName);
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
                        expectingLanguageValue = false;
                        continue;
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
                    // A valid primary state without this newer property is authoritative.
                    return true;
                }
            }
        }
        catch
        {
            // Language preference is non-critical. Normal state loading still owns
            // corruption/recovery reporting; startup localization simply falls back.
            return false;
        }
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
        return normalized == System
            ? systemCulture
            : CultureInfo.GetCultureInfo(normalized);
    }
}
