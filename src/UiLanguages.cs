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
    // Ordinary setting edits remain restart-based. A workspace boundary can replace the
    // effective culture after its old windows end, independent of async ExecutionContext flow.
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static readonly string StartupPreference = LoadPersistedPreferenceCore();

    // A regular setting change takes effect after restart or a complete workspace handoff.
    // EffectiveCulture owns user-visible number/date formatting and parsing; EffectiveUiCulture
    // owns resource lookup plus WPF language/shaping. Persistence and protocols choose explicitly.
    public static CultureInfo EffectiveCulture { get; private set; } =
        ResolveCulture(StartupPreference, SystemCulture);

    public static CultureInfo EffectiveUiCulture { get; private set; } =
        ResolveCulture(StartupPreference, SystemUiCulture);

    // A workspace switch has already ended the previous UI/session generation. Ordinary
    // setting edits retain their restart contract; only this full lifecycle boundary changes it.
    internal static void ApplyWorkspacePreference(string preference)
    {
        EffectiveCulture = ResolveCulture(preference, SystemCulture);
        EffectiveUiCulture = ResolveCulture(preference, SystemUiCulture);
        CultureInfo.DefaultThreadCurrentCulture = EffectiveCulture;
        CultureInfo.DefaultThreadCurrentUICulture = EffectiveUiCulture;
        CultureInfo.CurrentCulture = EffectiveCulture;
        CultureInfo.CurrentUICulture = EffectiveUiCulture;
    }

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
