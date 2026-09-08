using System.Globalization;
using System.Text;

namespace PaperTodo;

internal readonly record struct MarkdownListContinuationPlan(
    int MarkerStart,
    int ContentStart,
    int EmptyContentStart,
    string Continuation);

/// <summary>
/// 依据统一容器前缀选择当前行最内层列表。外层列表在新行变为内容缩进，外层引用继续保留；
/// 仅最内层列表 marker 被续写或递增。
/// </summary>
internal static class MarkdownListEditing
{
    public static bool TryBuildContinuationPlan(
        string lineText,
        int absoluteLineStart,
        MarkdownSemanticSnapshot snapshot,
        out MarkdownListContinuationPlan plan)
    {
        plan = default;
        ArgumentNullException.ThrowIfNull(lineText);
        ArgumentNullException.ThrowIfNull(snapshot);

        var absoluteLineEnd = absoluteLineStart + lineText.Length;
        var container = MarkdownContainerPrefix.Parse(
            lineText,
            snapshot,
            absoluteLineStart,
            absoluteLineEnd);

        var ownerIndex = -1;
        for (var index = container.Tokens.Count - 1; index >= 0; index--)
        {
            if (container.Tokens[index].IsList)
            {
                ownerIndex = index;
                break;
            }
        }

        if (ownerIndex < 0 ||
            container.MissingQuoteLevels > 0 ||
            ownerIndex != container.InnermostTokenIndex)
        {
            // 最内层是引用时由引用续行接管；没有真实列表 marker 时不猜测。
            return false;
        }

        var owner = container.Tokens[ownerIndex];
        var continuation = new StringBuilder(lineText.Length + 8);
        var cursor = 0;
        for (var index = 0; index <= ownerIndex; index++)
        {
            var token = container.Tokens[index];
            AppendRange(continuation, lineText, cursor, token.MarkerStart);
            if (index < ownerIndex)
            {
                if (token.IsQuote)
                {
                    continuation.Append("> ");
                }
                else
                {
                    AppendListContentIndent(
                        continuation,
                        lineText,
                        token.MarkerStart,
                        token.ContentStart);
                }
            }
            else
            {
                if (!AppendOwnerMarker(continuation, lineText, token))
                {
                    return false;
                }
                AppendRange(continuation, lineText, token.MarkerEnd, token.ContentStart);
            }

            cursor = token.ContentStart;
        }

        var hasTask = container.TaskOwnerTokenIndex == ownerIndex &&
            container.TaskMarkerStart == owner.ContentStart;
        var emptyContentStart = owner.ContentStart;
        if (hasTask)
        {
            continuation.Append("[ ] ");
            emptyContentStart = container.VisualIndentEnd;
        }

        plan = new MarkdownListContinuationPlan(
            owner.MarkerStart,
            owner.ContentStart,
            emptyContentStart,
            continuation.ToString());
        return true;
    }

    private static bool AppendOwnerMarker(
        StringBuilder builder,
        string lineText,
        MarkdownContainerPrefixToken owner)
    {
        if (owner.Kind == MarkdownContainerPrefixKind.UnorderedList)
        {
            AppendRange(builder, lineText, owner.MarkerStart, owner.MarkerEnd);
            return true;
        }

        var delimiterIndex = owner.MarkerEnd - 1;
        if (delimiterIndex <= owner.MarkerStart ||
            delimiterIndex >= lineText.Length ||
            lineText[delimiterIndex] is not ('.' or ')'))
        {
            return false;
        }

        var numberText = lineText[owner.MarkerStart..delimiterIndex];
        if (!long.TryParse(
                numberText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) ||
            number == long.MaxValue)
        {
            return false;
        }

        builder.Append((number + 1).ToString(CultureInfo.InvariantCulture));
        builder.Append(lineText[delimiterIndex]);
        return true;
    }

    private static void AppendListContentIndent(
        StringBuilder builder,
        string text,
        int start,
        int end)
    {
        for (var index = Math.Clamp(start, 0, text.Length);
             index < Math.Clamp(end, 0, text.Length);
             index++)
        {
            builder.Append(char.IsWhiteSpace(text[index]) ? text[index] : ' ');
        }
    }

    private static void AppendRange(
        StringBuilder builder,
        string text,
        int start,
        int end)
    {
        var normalizedStart = Math.Clamp(start, 0, text.Length);
        var normalizedEnd = Math.Clamp(end, normalizedStart, text.Length);
        if (normalizedEnd > normalizedStart)
        {
            builder.Append(text, normalizedStart, normalizedEnd - normalizedStart);
        }
    }
}
