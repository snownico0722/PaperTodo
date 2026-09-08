using System.Text;

namespace PaperTodo;

/// <summary>
/// 只生成显示层/显式 Enter 续行需要的引用前缀文本。Markdown 结构识别由 Markdig 语义快照负责，
/// 此处不再解析源码中的 `>`。
/// </summary>
internal static class MarkdownQuoteMarkers
{
    internal static string RepeatMarkerPrefix(int level)
    {
        if (level <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(level * 2);
        for (var index = 0; index < level; index++)
        {
            buffer.Append("> ");
        }

        return buffer.ToString();
    }
}
