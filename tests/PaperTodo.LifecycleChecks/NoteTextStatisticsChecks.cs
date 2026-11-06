using PaperTodo;

internal static class NoteTextStatisticsChecks
{
    internal static void Run()
    {
        Total("", new(1, 0, 0));
        Total("中😀 \r\n\t", new(2, 2, 2));
        Total("*中* [a](u)", new(1, 2, 9));
        Total("a\r\n\r\nb\n", new(4, 2, 2));
        Body("# 标题\n**中文** [abc](https://example.com)\n![](i:1)", 7);
        Body("中文 abc😀\n\t", 6);
        Body("![image|100%](i:123)", 5);
        Body("![image|50%](i:123){width=75%}", 5);
        Body("![image|100%](i:123)\n![second|30%](i:456){width=75%}", 11);
        Body("标题\n===\n\n- [x] 甲\n> 乙\n\n---", 4);
        Body("\\*a\\* &amp; <b>中</b>", 5);
        Body("&NotEqualTilde;", 2);
        Body("<div>foo</div>", 14);
        Body("abc **bad", 8);
        Body("[label][ref]\n\n[ref]: https://example.com", 5);
        Body("<https://a>", 9);
        Body("```cs\n**x**\n```", 5);
        Body("```cs\n**x**", 5);
        Body("> ```\n> **x**\n> ```", 5);
        Body("    **x**\n\t**y**", 10);
        Body("`a **b**`", 6);
        Body("```\n![image|50%](i:1)\n```", 17);

        const string markdown = "**中文** [abc](https://example.com)\n![image|50%](i:123){width=75%}\n```\n**x**\n```";
        var counts = NoteTextStatistics.Create(markdown);
        foreach (var (fragment, expected) in new[]
        {
            ("中文", 2), ("**", 0), ("abc", 3), ("https://example.com", 0),
            ("image", 5), ("|50%", 0), ("i:123", 0), ("{width=75%}", 0), ("**x**", 5)
        })
            Equal(expected, counts.Select(markdown.IndexOf(fragment, StringComparison.Ordinal), fragment.Length).Body,
                "Selected " + fragment);
        Equal(counts.Total, counts.Select(0, markdown.Length), "Whole selection");
        Equal(default(NoteTextCounts), counts.Select(2, 0), "No selection");
        var lines = NoteTextStatistics.Create("a\r\n\r\nb\n");
        Equal(new NoteTextCounts(1, 1, 1), lines.Select(0, 3), "Selected trailing CRLF");
        Equal(new NoteTextCounts(1, 0, 0), lines.Select(3, 2), "Selected empty line");
        Equal(new NoteTextCounts(1, 1, 1), lines.Select(5, 1), "Selected final text line");
        Equal(lines.Total, lines.Select(0, 7), "Whole selection with trailing newline");
    }

    private static void Total(string text, NoteTextCounts expected)
        => Equal(expected, NoteTextStatistics.Create(text).Total, text);
    private static void Body(string text, int expected)
        => Equal(expected, NoteTextStatistics.Count(text), text);
    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}; got {actual}");
    }
}
