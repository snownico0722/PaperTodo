using System.Runtime.CompilerServices;
using PaperTodo;

internal static class IncrementalCollapseChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        // Moving the caret may update incrementally or rebuild. Only the resulting hidden
        // source positions matter; redraw flags and the exact range segmentation do not.
        foreach (var source in new[]
        {
            "# Title ##\n\npara **bold** *em* `code` [link](https://e.com) ~~gone~~ esc \\x end\n",
            "- item **b**\n> quote `c`\n# Closer ##\n\n```\nvar a = 1;\n```\nplain tail",
            "# H\r\nnested **outer *inner* outer** and `x`\r\nafter",
            "# a **b**\nrest of the line",
            "alpha **beta**",
            "<b>1</b>", "<b><i>x</i></b>", "<b>1</b><i>2</i>", "<b></b>",
            "a <https://example.com> z", "x<b>1</b>y",
            "<a href=\"https://e.com\">anchor</a>", "<b>a **bold** b</b>"
        })
        {
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var lineStarts = MarkdownSemanticSnapshot.BuildLineStarts(source);
            var table = MarkdownCollapseTable.Build(snapshot, source, new MarkdownCaretReveal(0, 0));
            for (var offset = 0; offset <= source.Length; offset++)
            {
                var caret = new MarkdownCaretReveal(offset, MarkdownSemanticCollapseLayout.FindLine(lineStarts, offset));
                table.SyncTo(caret);
                var expected = new bool[source.Length];
                var actual = new bool[source.Length];
                foreach (var range in MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(snapshot, source, caret))
                    Array.Fill(expected, true, range.Start, range.Length);
                foreach (var range in table.Runs)
                    Array.Fill(actual, true, range.Start, range.Length);
                if (!expected.SequenceEqual(actual))
                    throw new InvalidOperationException($"Collapse result differs at caret {offset}: {source}");
            }
        }
        Console.WriteLine("PASS caret movement preserves collapsed source positions");
    }
}
