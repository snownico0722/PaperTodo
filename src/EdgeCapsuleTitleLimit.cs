using System.Globalization;

namespace PaperTodo;

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
