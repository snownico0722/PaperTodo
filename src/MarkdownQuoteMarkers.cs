using System.Collections.Generic;

namespace PaperTodo;

/// <summary>读取行首显式引用标记，供显示和用户主动按 Enter 的续行操作使用；不修正已有源码。</summary>
internal static class MarkdownQuoteMarkers
{
    /// <summary>扫描 [start,end) 内行首连续引用 marker（每段前最多 3 空格、`&gt;` 后可跟一空格/tab），返回绝对偏移区间。</summary>
    internal static IReadOnlyList<(int Start, int End)> EnumerateMarkers(string source, int start, int end)
    {
        var markers = new List<(int, int)>();
        var index = start;
        while (index < end)
        {
            var spaces = 0;
            while (index < end && spaces < 3 && source[index] == ' ')
            {
                index++;
                spaces++;
            }

            if (index >= end || source[index] != '>')
            {
                break;
            }

            var markerStart = index;
            index++;
            if (index < end && source[index] is ' ' or '\t')
            {
                index++;
            }

            markers.Add((markerStart, index));
        }

        return markers;
    }

    internal static string RepeatMarkerPrefix(int level)
    {
        var buffer = new System.Text.StringBuilder(level * 2);
        for (var index = 0; index < level; index++)
        {
            buffer.Append("> ");
        }

        return buffer.ToString();
    }
}
