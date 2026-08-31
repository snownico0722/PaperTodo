using System.Globalization;

namespace PaperTodo;

public static class PaperTitles
{
    // Hard storage / edit cap in Unicode text elements (≈ full-width characters for CJK).
    // Titles are never stored longer than this regardless of the user setting.
    public const int MaxTitleLength = 20;

    // User-configurable display/edit cap (Settings → 标题最大长度), within MaxTitleLength.
    public const int DefaultMaxTitleLength = 6;
    public const int MinConfigurableTitleLength = 2;
    public const int MaxConfigurableTitleLength = MaxTitleLength;

    public static int NormalizeMaxTitleLength(int value)
    {
        if (value <= 0)
        {
            return DefaultMaxTitleLength;
        }

        return Math.Clamp(value, MinConfigurableTitleLength, MaxConfigurableTitleLength);
    }

    public static string DefaultTitle(string paperType, int number)
    {
        var prefix = DefaultTitlePrefix(paperType);
        return prefix + Math.Max(1, number).ToString(CultureInfo.InvariantCulture);
    }

    public static string DefaultTitlePrefix(string paperType)
    {
        return paperType == PaperTypes.Note
            ? Strings.Get("PaperKindNote")
            : Strings.Get("PaperKindTodo");
    }

    public static string CleanCustomTitle(string? title)
    {
        return CleanCustomTitle(title, MaxTitleLength);
    }

    public static string CleanCustomTitle(string? title, int maxLength)
    {
        var cleaned = (title ?? "").Trim();
        cleaned = string.Join("", cleaned.Where(ch => !char.IsControl(ch)));
        return TakeTextElements(cleaned, Math.Clamp(maxLength, 1, MaxTitleLength));
    }

    public static string EffectiveTitle(PaperData paper, int fallbackNumber)
    {
        var title = CleanCustomTitle(paper.Title);
        return string.IsNullOrWhiteSpace(title)
            ? DefaultTitle(paper.Type, fallbackNumber)
            : title;
    }

    public static string CapsuleText(PaperData paper, int fallbackNumber)
    {
        return EffectiveTitle(paper, fallbackNumber);
    }

    private static string TakeTextElements(string text, int maxLength)
    {
        var indexes = StringInfo.ParseCombiningCharacters(text);
        if (indexes.Length <= maxLength)
        {
            return text;
        }

        return text[..indexes[maxLength]];
    }
}
