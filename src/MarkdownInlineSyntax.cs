using System.Text;

namespace PaperTodo;

internal static class MarkdownInlineSyntax
{
    public static bool IsEscaped(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
        {
            return false;
        }

        var slashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            slashes++;
        }
        return (slashes & 1) != 0;
    }

    public static bool IsEscapable(char value) =>
        value is >= '!' and <= '/' or
        >= ':' and <= '@' or
        >= '[' and <= '`' or
        >= '{' and <= '~';

    public static int IndexOfUnescaped(string text, char value, int startIndex)
    {
        var search = Math.Max(0, startIndex);
        while (search < text.Length)
        {
            var index = text.IndexOf(value, search);
            if (index < 0 || !IsEscaped(text, index))
            {
                return index;
            }
            search = index + 1;
        }
        return -1;
    }

    public static int IndexOfUnescaped(string text, string value, int startIndex)
    {
        var search = Math.Max(0, startIndex);
        while (search <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, search, StringComparison.Ordinal);
            if (index < 0 || !IsEscaped(text, index))
            {
                return index;
            }
            search = index + 1;
        }
        return -1;
    }

    public static string Unescape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] != '\\')
            {
                builder.Append(text[index++]);
                continue;
            }

            var runStart = index;
            while (index < text.Length && text[index] == '\\')
            {
                index++;
            }

            var slashCount = index - runStart;
            builder.Append('\\', slashCount / 2);
            if (index < text.Length && IsEscapable(text[index]))
            {
                if ((slashCount & 1) != 0)
                {
                    builder.Append(text[index++]);
                }
                continue;
            }

            if ((slashCount & 1) != 0)
            {
                builder.Append('\\');
            }
        }
        return builder.ToString();
    }
}
