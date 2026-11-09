using System.Globalization;

namespace PaperTodo;

public static class PaperTitles
{
    // Title limits are full-width character equivalents: CJK/full-width = 2 units,
    // Latin/half-width = 1 unit. The configured limit is expressed in 2-unit characters.
    public const int MaxTitleLength = 20;

    // New-install default. Existing saved preferences retain their configured value.
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
        var cleaned = NormalizeCustomTitle(title);
        return TakeByDisplayWidth(cleaned, Math.Clamp(maxLength, 1, MaxTitleLength) * 2);
    }

    internal static bool ExceedsTitleLengthLimit(string? title, int maxLength)
    {
        var cleaned = NormalizeCustomTitle(title);
        var limit = Math.Clamp(maxLength, 1, MaxTitleLength) * 2;
        return EdgeCapsuleLayout.DisplayWidth(cleaned) > limit;
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

    // Display limits never rewrite the persisted title. Editing and external reads
    // still use EffectiveTitle; only compact UI labels use this projection.
    public static string DisplayTitle(PaperData paper, int fallbackNumber, int maxLength) =>
        CleanCustomTitle(EffectiveTitle(paper, fallbackNumber), maxLength);

    private static string NormalizeCustomTitle(string? title)
    {
        var cleaned = (title ?? "").Trim();
        return string.Join("", cleaned.Where(ch => !char.IsControl(ch)));
    }

    private static string TakeByDisplayWidth(string text, int maximumWidth)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            width += EdgeCapsuleLayout.DisplayWidth((string)elements.Current);
            if (width > maximumWidth)
            {
                // Never cut in the middle of a combining sequence or emoji cluster.
                return text[..elements.ElementIndex];
            }
        }

        return text;
    }
}

// Preserve the data.json wire meaning of 0 (unlimited). -1 is the new UI "0 characters"
// choice, so upgrading an existing unlimited configuration needs no ambiguous migration.
internal static class EdgeCapsuleTitleLimit
{
    public const int Unlimited = 0;
    public const int Hidden = -1;

    public static int Normalize(int value) => value < Hidden
        ? Unlimited
        : Math.Min(value, PaperTitles.MaxConfigurableTitleLength);

    public static int Step(int value, bool increase)
    {
        value = Normalize(value);
        var index = value switch { Unlimited => 0, Hidden => 1, _ => value + 1 };
        var count = PaperTitles.MaxConfigurableTitleLength + 2;
        index = (index + (increase ? 1 : count - 1)) % count;
        return index switch { 0 => Unlimited, 1 => Hidden, _ => index - 1 };
    }

    public static string TextForMeasure(string text, int value)
    {
        value = Normalize(value);
        if (value == Hidden) return "";
        if (value == Unlimited || string.IsNullOrEmpty(text)) return text;
        var indexes = StringInfo.ParseCombiningCharacters(text);
        return indexes.Length <= value ? text : text[..indexes[value]];
    }
}
