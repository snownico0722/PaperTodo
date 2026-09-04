using System.Runtime.CompilerServices;
using PaperTodo;

internal static class RevealChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("None caret reveals nothing", () =>
        {
            False(
                MarkdownSemanticReveal.RevealMarker(
                    MarkdownCaretReveal.None, 0, 0, 2, MarkdownSemanticSpanKind.Heading),
                "None caret must not reveal block marker");
            False(
                MarkdownSemanticReveal.RevealRange(MarkdownCaretReveal.None, 0, 5),
                "None caret must not reveal range");
        });

        Check("Inline range reveals only inside span", () =>
        {
            const string source = "before **bold** after";
            var strong = SingleSpan(
                MarkdownSemanticSnapshot.Parse(source),
                MarkdownSemanticSpanKind.Strong);
            var caretLine = LineForOffset(source, strong.Start + 4);
            var caret = new MarkdownCaretReveal(strong.Start + 4, caretLine);

            True(
                MarkdownSemanticReveal.RevealMarker(
                    caret, caretLine, strong.Start, strong.Length,
                    MarkdownSemanticSpanKind.Strong, strong.Start, strong.End),
                "caret inside strong content reveals delimiters");
            var atOpening = new MarkdownCaretReveal(strong.Start, LineForOffset(source, strong.Start));
            True(
                MarkdownSemanticReveal.RevealMarker(
                    atOpening, atOpening.CaretLineZeroBased, strong.Start, strong.Length,
                    MarkdownSemanticSpanKind.Strong, strong.Start, strong.End),
                "caret on the opening marker is inside the span");

            var before = new MarkdownCaretReveal(strong.Start - 1, LineForOffset(source, strong.Start - 1));
            False(
                MarkdownSemanticReveal.RevealMarker(
                    before, before.CaretLineZeroBased, strong.Start, strong.Length,
                    MarkdownSemanticSpanKind.Strong, strong.Start, strong.End),
                "caret before the span must stay hidden");
            var atEnd = new MarkdownCaretReveal(strong.End, LineForOffset(source, strong.End));
            True(
                MarkdownSemanticReveal.RevealMarker(
                    atEnd, atEnd.CaretLineZeroBased, strong.Start, strong.Length,
                    MarkdownSemanticSpanKind.Strong, strong.Start, strong.End),
                "caret at the right boundary reveals the span for editing");
            var beyondEnd = new MarkdownCaretReveal(strong.End + 1, LineForOffset(source, strong.End + 1));
            False(
                MarkdownSemanticReveal.RevealMarker(
                    beyondEnd, beyondEnd.CaretLineZeroBased, strong.Start, strong.Length,
                    MarkdownSemanticSpanKind.Strong, strong.Start, strong.End),
                "caret one step into the following plain text stays hidden");
        });

        Check("Heading marker reveals on its own row", () =>
        {
            const string source = "# Title\n\nplain";
            var heading = SingleSpan(
                MarkdownSemanticSnapshot.Parse(source),
                MarkdownSemanticSpanKind.Heading);
            var caret = new MarkdownCaretReveal(heading.Start + 3, 0);
            True(
                MarkdownSemanticReveal.RevealMarker(
                    caret, 0, heading.Start, 2, MarkdownSemanticSpanKind.Heading),
                "caret in heading text reveals atx marker");

            var plainLine = LineForOffset(source, source.IndexOf("plain", StringComparison.Ordinal));
            var plainCaret = new MarkdownCaretReveal(
                source.IndexOf("plain", StringComparison.Ordinal), plainLine);
            False(
                MarkdownSemanticReveal.RevealMarker(
                    plainCaret, 0, heading.Start, 2, MarkdownSemanticSpanKind.Heading),
                "caret in the next paragraph must not reveal heading marker");
        });

        Check("Setext marker reveals only on underline row", () =>
        {
            const string source = "Title\n=====";
            var underlineStart = source.IndexOf('=', StringComparison.Ordinal);
            var underlineLength = source.Length - underlineStart;
            var caretOnContent = new MarkdownCaretReveal(1, 0);
            False(
                MarkdownSemanticReveal.RevealMarker(
                    caretOnContent, 1, underlineStart, underlineLength,
                    MarkdownSemanticSpanKind.SetextMarker),
                "editing setext content keeps underline source hidden");
            var caretOnUnderline = new MarkdownCaretReveal(underlineStart + 2, 1);
            True(
                MarkdownSemanticReveal.RevealMarker(
                    caretOnUnderline, 1, underlineStart, underlineLength,
                    MarkdownSemanticSpanKind.SetextMarker),
                "caret on underline row reveals setext marker");
        });

        Check("Quote markers reveal per row", () =>
        {
            const string source = "> alpha\n> beta";
            var caretLine0 = new MarkdownCaretReveal(4, 0);
            True(
                MarkdownSemanticReveal.RevealMarker(caretLine0, 0, 0, 1, MarkdownSemanticSpanKind.Quote),
                "editing quote row reveals its own marker");
            False(
                MarkdownSemanticReveal.RevealMarker(caretLine0, 1, 7, 1, MarkdownSemanticSpanKind.Quote),
                "quote marker on another row stays hidden");
        });

        Check("Bullets reveal per item row", () =>
        {
            const string source = "- one\n- two";
            var secondLineStart = source.IndexOf('\n') + 1;
            var caretSecond = new MarkdownCaretReveal(source.IndexOf("two", StringComparison.Ordinal), 1);
            False(
                MarkdownSemanticReveal.RevealMarker(
                    caretSecond, 0, 0, 1, MarkdownSemanticSpanKind.UnorderedListMarker),
                "first item bullet hidden while editing second item");
            True(
                MarkdownSemanticReveal.RevealMarker(
                    caretSecond, 1, secondLineStart, 1, MarkdownSemanticSpanKind.UnorderedListMarker),
                "own item bullet reveals while editing it");
        });

        Check("List continuation row does not reveal earlier bullet", () =>
        {
            const string source = "1. first\n   continuation";
            var caretContinuation = new MarkdownCaretReveal(
                source.IndexOf("continuation", StringComparison.Ordinal), 1);
            False(
                MarkdownSemanticReveal.RevealMarker(
                    caretContinuation, 0, 0, 2, MarkdownSemanticSpanKind.OrderedListMarker),
                "bullet on first row stays hidden while caret on continuation row");
        });

        Check("Fenced fences stay hidden while editing code content", () =>
        {
            const string source = "```\nvar x = 1;\n```";
            var caretContent = new MarkdownCaretReveal(
                source.IndexOf("var", StringComparison.Ordinal), 1);
            False(
                MarkdownSemanticReveal.RevealMarker(
                    caretContent, 0, 0, 3, MarkdownSemanticSpanKind.FencedCodeOpening),
                "opening fence hidden while caret is inside code content");
            var caretOpening = new MarkdownCaretReveal(1, 0);
            True(
                MarkdownSemanticReveal.RevealMarker(
                    caretOpening, 0, 0, 3, MarkdownSemanticSpanKind.FencedCodeOpening),
                "caret on the opening fence row reveals it");
        });

        Check("Task marker reveals while editing item text", () =>
        {
            const string source = "- [ ] todo";
            var taskStart = source.IndexOf("[", StringComparison.Ordinal);
            var caretText = new MarkdownCaretReveal(source.IndexOf("todo", StringComparison.Ordinal), 0);
            True(
                MarkdownSemanticReveal.RevealMarker(
                    caretText, 0, taskStart, 3, MarkdownSemanticSpanKind.TaskListMarker),
                "editing task content reveals the source checkbox");
        });

        Check("Range kind classification", () =>
        {
            True(MarkdownSemanticReveal.IsRangeKind(MarkdownSemanticSpanKind.Strong), "strong is range kind");
            True(MarkdownSemanticReveal.IsRangeKind(MarkdownSemanticSpanKind.InlineCode), "inline code is range kind");
            False(MarkdownSemanticReveal.IsRangeKind(MarkdownSemanticSpanKind.Heading), "heading is not a range kind");
            False(MarkdownSemanticReveal.IsRangeKind(MarkdownSemanticSpanKind.HtmlMarker), "html marker is not a range kind");
        });

        Check("Caret line computed across CR-only source", () =>
        {
            const string source = "# heading\rplain";
            var plainOffset = source.IndexOf("plain", StringComparison.Ordinal);
            Equal(1, LineForOffset(source, plainOffset), "CR-only caret line");
            var caret = new MarkdownCaretReveal(plainOffset, 1);
            False(
                MarkdownSemanticReveal.RevealMarker(caret, 0, 0, 1, MarkdownSemanticSpanKind.Heading),
                "heading marker on another CR line stays hidden");
        });

        Check("HasRevealOnLine flips with caret row", () =>
        {
            const string source = "# Title\nplain";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            True(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, "# Title", 0, 0, new MarkdownCaretReveal(3, 0)),
                "caret in heading text reveals a control marker on the row");
            False(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, "plain", source.IndexOf("plain", StringComparison.Ordinal), 1,
                    new MarkdownCaretReveal(source.IndexOf("plain", StringComparison.Ordinal), 1)),
                "next plain paragraph has no revealed control marker");
        });

        Check("HasRevealOnLine quote row vs plain row", () =>
        {
            const string source = "> alpha\nplain";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            True(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, "> alpha", 0, 0, new MarkdownCaretReveal(2, 0)),
                "quote row caret reveals its > marker");
            var plainStart = source.IndexOf("plain", StringComparison.Ordinal);
            False(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, "plain", plainStart, 1, new MarkdownCaretReveal(plainStart + 1, 1)),
                "non-quote plain row reveals nothing");
        });

        Check("HasRevealOnLine fenced code content row stays clean", () =>
        {
            const string source = "```\ncode\n```";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var codeStart = source.IndexOf("code", StringComparison.Ordinal);
            False(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, "code", codeStart, 1, new MarkdownCaretReveal(codeStart + 2, 1)),
                "caret inside code content does not reveal fence rows");
            True(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, "```", 0, 0, new MarkdownCaretReveal(1, 0)),
                "caret on the opening fence row reveals it");
        });

        Check("HasRevealOnLine strong span enters and leaves", () =>
        {
            const string source = "before **bold** after";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var strong = SingleSpan(snapshot, MarkdownSemanticSpanKind.Strong);
            var inside = new MarkdownCaretReveal(
                strong.Start + 4, LineForOffset(source, strong.Start + 4));
            True(
                MarkdownSemanticReveal.HasRevealOnLine(snapshot, source, 0, inside.CaretLineZeroBased, inside),
                "caret inside the strong content reveals delimiters on the row");
            var before = new MarkdownCaretReveal(
                strong.Start - 1, LineForOffset(source, strong.Start - 1));
            False(
                MarkdownSemanticReveal.HasRevealOnLine(snapshot, source, 0, before.CaretLineZeroBased, before),
                "caret before the strong span reveals nothing");
        });

        Check("HasRevealOnLine detects revealed link", () =>
        {
            const string source = "see [label](url) end";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var labelStart = source.IndexOf("label", StringComparison.Ordinal);
            True(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, source, 0, 0, new MarkdownCaretReveal(labelStart, 0)),
                "caret on the label reveals the link syntax");
            False(
                MarkdownSemanticReveal.HasRevealOnLine(
                    snapshot, source, 0, 0, new MarkdownCaretReveal(1, 0)),
                "caret in leading plain text reveals no link");
        });
    }

    /// <summary>由源文本与绝对偏移计算零基行号（与快照 lineStarts 规则一致）。</summary>
    private static int LineForOffset(string source, int offset)
    {
        var normalized = Math.Clamp(offset, 0, source.Length);
        var line = 0;
        for (var index = 0; index < normalized; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }
                line++;
            }
            else if (source[index] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private static MarkdownSemanticSpan SingleSpan(
        MarkdownSemanticSnapshot snapshot,
        MarkdownSemanticSpanKind kind)
    {
        var matches = snapshot.Spans.Where(span => span.Kind == kind).ToArray();
        Equal(1, matches.Length, $"{kind} span count");
        return matches[0];
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
