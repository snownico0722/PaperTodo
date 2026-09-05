using System.Runtime.CompilerServices;
using PaperTodo;

/// <summary>
/// 增量折叠表 MarkdownCollapseTable 的正确性测试：
/// 1) oracle 步进——光标从 0 逐字符推进到文末，每步断言增量维护的 Runs 与整篇重算
///    ComputeCollapsedRuns 完全一致（摘除/插入/合并/劈分任何错误都会暴露）；
/// 2) 机制行为——同区间移动不产生显灵变化（零重算/零重排），翻越边界才变化。
/// </summary>
internal static class IncrementalCollapseChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Oracle: single-line constructs step-through", () =>
        {
            const string source =
                "# Title ##\n\n" +
                "para with **bold** and *em* and `code` and [link](https://e.com) and ~~gone~~ and esc \\x end\n";
            StepThrough(source);
        });

        Check("Oracle: block/quote/fence/list constructs step-through", () =>
        {
            const string source =
                "- item **b**\n" +
                "> quote with `c`\n" +
                "# Closer ##\n\n" +
                "```\nvar a = 1;\n```\n" +
                "plain tail";
            StepThrough(source);
        });

        Check("Oracle: CRLF + nested emphasis step-through", () =>
        {
            const string source =
                "# H\r\n" +
                "nested **outer *inner* outer** and `x`\r\n" +
                "after";
            StepThrough(source);
        });

        Check("Oracle: marker at line end crossing into next line", () =>
        {
            // 加粗的闭区间 End 恰好落在换行前：光标从 End（行 0）移到 End+1（行 1）须翻越边界。
            const string source = "# a **b**\nrest of the line";
            StepThrough(source);
        });

        Check("Oracle: marker ends exactly at EOF", () =>
        {
            // 文档结尾就是闭合标记：光标走到 EOF（== End）等边界都须与整篇重算一致。
            const string source = "alpha **beta** and # heading";
            StepThrough(source);
        });

        Check("Mechanism: inside bold moves produce no reveal change", () =>
        {
            const string source = "aaa **bold** bbb\nplain";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var table = MarkdownCollapseTable.Build(snapshot, source, new MarkdownCaretReveal(0, 0));
            var strong = snapshot.Spans.Single(s => s.Kind == MarkdownSemanticSpanKind.Strong);
            var start = strong.Start;              // 进入加粗：offset==Start 起显灵
            var end = strong.End;                  // 闭区间：offset<=End 仍显灵

            // 在同区间内部（含两端边界）逐格移动：显灵集合不变 → 不需要重排。
            var first = table.SyncTo(new MarkdownCaretReveal(start, 0));
            True(first.VisualChanged, "crossing into bold must schedule a redraw");
            for (var offset = start + 1; offset <= end; offset++)
            {
                var change = table.SyncTo(new MarkdownCaretReveal(offset, 0));
                False(change.VisualChanged, $"inside bold offset {offset} must not change visuals");
            }

            // 跨出边界：显灵消失 → 需要重排。
            var exit = table.SyncTo(new MarkdownCaretReveal(end + 1, 0));
            True(exit.VisualChanged, "leaving bold must schedule a redraw");

            // 同一行纯文本区移动 → 无变化。
            var plainA = table.SyncTo(new MarkdownCaretReveal(15, 0));
            False(plainA.VisualChanged, "plain text on the same line must not change visuals");

            // 跨到无格式行 → 无变化（旧行光标已离开任何显灵单元）。
            var line = LineOf(source, source.IndexOf("plain", StringComparison.Ordinal));
            var cross = table.SyncTo(new MarkdownCaretReveal(source.IndexOf("plain", StringComparison.Ordinal), line));
            False(cross.VisualChanged, "moving to a formatting-free line must not change visuals");
        });

        Check("Mechanism: long doc near end arrows stay clean", () =>
        {
            var builder = new System.Text.StringBuilder();
            for (var index = 0; index < 60; index++)
            {
                builder.Append("# H ").Append(index).Append('\n');
                builder.Append("line **b** `c` [l](https://e.com/) ").Append(index).Append('\n');
            }

            // 尾部一大段无格式内容，模拟“长笔记靠近文末持续按方向键”。
            var tailStart = builder.Length;
            builder.Append("\n\n").Append('x', 400);
            var source = builder.ToString();
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var startLine = LineOf(source, tailStart);
            var table = MarkdownCollapseTable.Build(
                snapshot, source, new MarkdownCaretReveal(tailStart, startLine));

            for (var offset = tailStart + 1; offset <= source.Length; offset++)
            {
                var change = table.SyncTo(new MarkdownCaretReveal(offset, LineOf(source, offset)));
                False(change.VisualChanged, $"near-end offset {offset} must not change visuals");
            }
        });
    }

    /// <summary>光标逐字符推进，逐步断言增量 Runs 与整篇重算一致。</summary>
    private static void StepThrough(string source)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var lineStarts = MarkdownSemanticCollapseLayout.BuildLineStarts(source);
        var caret = new MarkdownCaretReveal(0, 0);
        var table = MarkdownCollapseTable.Build(snapshot, source, caret);
        AssertRunsMatch(source, snapshot, table, caret);

        for (var offset = 1; offset <= source.Length; offset++)
        {
            caret = new MarkdownCaretReveal(offset, MarkdownSemanticCollapseLayout.FindLine(lineStarts, offset));
            table.SyncTo(caret);
            AssertRunsMatch(source, snapshot, table, caret);
        }
    }

    private static void AssertRunsMatch(
        string source,
        MarkdownSemanticSnapshot snapshot,
        MarkdownCollapseTable table,
        MarkdownCaretReveal caret)
    {
        var expected = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(snapshot, source, caret);
        var actual = table.Runs;
        Equal(expected.Count, actual.Count, $"caret {caret.CaretOffset}: collapse run count");
        for (var index = 0; index < expected.Count; index++)
        {
            Equal(expected[index].Start, actual[index].Start, $"caret {caret.CaretOffset}: run {index} start");
            Equal(expected[index].End, actual[index].End, $"caret {caret.CaretOffset}: run {index} end");
            Equal(
                source.Substring(expected[index].Start, expected[index].Length),
                source.Substring(actual[index].Start, actual[index].Length),
                $"caret {caret.CaretOffset}: run {index} source matches");
        }
    }

    private static int LineOf(string source, int offset)
    {
        var starts = MarkdownSemanticCollapseLayout.BuildLineStarts(source);
        return MarkdownSemanticCollapseLayout.FindLine(starts, offset);
    }

    private static void Check(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FAIL {name}: {ex.Message}", ex);
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
