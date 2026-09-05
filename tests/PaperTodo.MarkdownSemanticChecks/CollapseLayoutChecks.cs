using System.Runtime.CompilerServices;
using PaperTodo;

internal static class CollapseLayoutChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("No-caret heading collapses opening marker", () =>
        {
            const string source = "# Title";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, MarkdownCaretReveal.None);
            EqualRuns(source, "(0,2)", runs);
        });

        Check("Heading closing marker collapses too", () =>
        {
            const string source = "# Hi #";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, MarkdownCaretReveal.None);
            EqualRuns(source, "(0,2);(5,6)", runs);
        });

        Check("Caret inside heading reveals source (no collapse)", () =>
        {
            const string source = "# Title";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var caret = new MarkdownCaretReveal(3, 0);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, caret);
            Equal(0, runs.Count, "heading collapse suppressed while caret in heading");
        });

        Check("Bold delimiters collapse outside caret", () =>
        {
            const string source = "a **b** c";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, MarkdownCaretReveal.None);
            EqualRuns(source, "(2,4);(5,7)", runs);
        });

        Check("Caret inside bold keeps delimiters visible", () =>
        {
            const string source = "a **b** c";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var caret = new MarkdownCaretReveal(4, 0);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, caret);
            Equal(0, runs.Count, "bold delimiters not collapsed while editing the span");
        });

        Check("Inline code delimiters collapse", () =>
        {
            const string source = "use `x` now";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, MarkdownCaretReveal.None);
            EqualRuns(source, "(4,5);(6,7)", runs);
        });

        Check("Explicit link syntax collapses around label", () =>
        {
            const string source = "see [x](http://e.com) end";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, MarkdownCaretReveal.None);
            // [、](、url、) 塌缩；label "x" 保留。
            var labelStart = source.IndexOf('x', StringComparison.Ordinal);
            var labelEnd = labelStart + 1;
            EqualRuns(source, $"({source.IndexOf('[', StringComparison.Ordinal)},{labelStart});({labelEnd},{source.IndexOf(')', StringComparison.Ordinal) + 1})", runs);
        });

        Check("Runs never overlap across kinds", () =>
        {
            const string source = "# **hi** and [x](http://e.com)";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                snapshot, source, MarkdownCaretReveal.None);
            for (var i = 1; i < runs.Count; i++)
            {
                True(runs[i].Start >= runs[i - 1].End, "collapse runs must be sorted & non-overlapping");
            }
        });
    }

    private static void EqualRuns(
        string source,
        string expected,
        IReadOnlyList<MarkdownCollapseRun> actual)
    {
        var parts = expected.Length == 0
            ? Array.Empty<string>()
            : expected.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Equal(parts.Length, actual.Count, "collapse run count");
        for (var i = 0; i < parts.Length; i++)
        {
            var range = parts[i].Trim('(', ')');
            var numbers = range.Split(',');
            var expectedStart = int.Parse(numbers[0], System.Globalization.CultureInfo.InvariantCulture);
            var expectedEnd = int.Parse(numbers[1], System.Globalization.CultureInfo.InvariantCulture);
            Equal(expectedStart, actual[i].Start, $"run {i} start");
            Equal(expectedEnd, actual[i].End, $"run {i} end");
            Equal(
                source.Substring(actual[i].Start, actual[i].Length),
                source.Substring(expectedStart, expectedEnd - expectedStart),
                $"run {i} source matches");
        }
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

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
