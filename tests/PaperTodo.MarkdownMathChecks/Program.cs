using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("inline math", InlineMath),
            ("single-line display math", SingleLineDisplayMath),
            ("multiline display math", MultilineDisplayMath),
            ("code regions exclude math", CodeRegionsExcludeMath),
            ("escaped and currency dollars stay text", DollarTextBoundaries),
            ("Markdown links exclude math", MarkdownLinksExcludeMath),
            ("CRLF display math", CrLfDisplayMath),
            ("unclosed display math stays source", UnclosedDisplayMath),
            ("block delimiter edits force exact parse", BlockDelimiterEditForcesExactParse)
        };

        foreach (var test in tests)
        {
            test.Run();
            Console.WriteLine($"PASS: {test.Name}");
        }

        Console.WriteLine($"Markdown math checks passed: {tests.Length}");
        return 0;
    }

    private static void InlineMath()
    {
        const string source = "Energy is $E=mc^2$.";
        var span = SingleMathSpan(source, MarkdownSemanticSpanKind.InlineMath);
        Assert(span.MarkerLength == 1, "Inline formula marker length must be one dollar.");
        Assert(
            source.Substring(span.Start, span.Length) == "$E=mc^2$",
            "Inline formula source range drifted.");
        Assert(
            MarkdownMathSource.TryExtract(source, span, out var formula, out var display) &&
            formula == "E=mc^2" &&
            !display,
            "Inline formula extraction failed.");
    }

    private static void SingleLineDisplayMath()
    {
        const string source = "$$\\frac{a+b}{c}$$";
        var span = SingleMathSpan(source, MarkdownSemanticSpanKind.BlockMath);
        Assert(span.MarkerLength == 2, "Display formula marker length must be two dollars.");
        Assert(
            MarkdownMathSource.TryExtract(source, span, out var formula, out var display) &&
            formula == "\\frac{a+b}{c}" &&
            display,
            "Single-line display formula extraction failed.");
    }

    private static void MultilineDisplayMath()
    {
        const string source = """
$$
\begin{aligned}
a &= b + c \\
d &= e + f
\end{aligned}
$$
""";
        var span = SingleMathSpan(source, MarkdownSemanticSpanKind.BlockMath);
        Assert(
            MarkdownMathSource.TryExtract(source, span, out var formula, out var display),
            "Multiline display formula extraction failed.");
        Assert(display, "Multiline formula must use display layout.");
        Assert(formula.Contains("\\begin{aligned}", StringComparison.Ordinal),
            "Aligned environment was lost.");
        Assert(formula.Contains('\n'), "Multiline formula was flattened.");
        Assert(formula.EndsWith("\\end{aligned}", StringComparison.Ordinal),
            "Closing aligned environment was lost.");
    }

    private static void CodeRegionsExcludeMath()
    {
        const string source = """
```latex
$x$
$$
y
$$
```

`$z$`
""";
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        Assert(
            !snapshot.Spans.Any(IsMath),
            "Math semantics must not escape fenced or inline code.");
    }

    private static void DollarTextBoundaries()
    {
        const string source = "Escaped \\$x\\$ and price $100 remain ordinary text.";
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        Assert(!snapshot.Spans.Any(IsMath), "Escaped/currency dollars were misread as math.");
    }

    private static void MarkdownLinksExcludeMath()
    {
        const string source = "[$x$](https://example.com)";
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        Assert(!snapshot.Spans.Any(IsMath), "Math escaped a Markdown-owned link range.");
        Assert(snapshot.Links.Count == 1, "The surrounding Markdown link was lost.");
    }

    private static void CrLfDisplayMath()
    {
        const string source = "$$\r\n\\begin{matrix}\r\n1 & 2 \\\\ 3 & 4\r\n\\end{matrix}\r\n$$";
        var span = SingleMathSpan(source, MarkdownSemanticSpanKind.BlockMath);
        Assert(
            MarkdownMathSource.TryExtract(source, span, out var formula, out var display) && display,
            "CRLF display formula extraction failed.");
        Assert(!formula.Contains('\r'), "CRLF formula was not normalized.");
        Assert(formula.Contains("\\begin{matrix}", StringComparison.Ordinal),
            "Matrix environment was lost.");
    }

    private static void UnclosedDisplayMath()
    {
        const string source = "$$\n\\frac{a}{b}";
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        Assert(!snapshot.Spans.Any(IsMath), "An unclosed display formula became semantic math.");
    }

    private static void BlockDelimiterEditForcesExactParse()
    {
        Assert(
            MarkdownMathIncremental.ChangeMayAffectDelimiterState(
                "prefix\n$\nbody",
                "prefix\n$$\nbody"),
            "Adding the second block delimiter dollar must force a full parse.");
        Assert(
            !MarkdownMathIncremental.ChangeMayAffectDelimiterState(
                "prefix\nordinary text",
                "prefix\nordinary texts"),
            "Ordinary long-note edits must retain the incremental path.");

        var prefix = new string('a', MarkdownSemanticDocument.FullParseThresholdChars + 200);
        var document = new TextDocument(prefix + "\nplain");
        using var semantics = new MarkdownSemanticDocument(document);
        var raised = false;
        MarkdownSourceChange? observed = default;
        semantics.SnapshotChanged += change =>
        {
            raised = true;
            observed = change;
        };

        document.Text = prefix + "\n$$\n\\begin{cases}x=1 \\\\ x=2\\end{cases}\n$$";
        Assert(raised, "Semantic change event was not raised.");
        Assert(observed == null, "A block-delimiter edit incorrectly used a local snapshot.");
        Assert(semantics.TryGetCurrent(out var snapshot), "Final semantic snapshot is unavailable.");
        Assert(snapshot.Spans.Any(span => span.Kind == MarkdownSemanticSpanKind.BlockMath),
            "The exact parse did not publish display math.");
    }

    private static MarkdownSemanticSpan SingleMathSpan(
        string source,
        MarkdownSemanticSpanKind kind)
    {
        var matches = MarkdownSemanticSnapshot.Parse(source)
            .Spans
            .Where(span => span.Kind == kind)
            .ToArray();
        Assert(matches.Length == 1,
            $"Expected exactly one {kind} span, found {matches.Length}.");
        return matches[0];
    }

    private static bool IsMath(MarkdownSemanticSpan span) =>
        span.Kind is MarkdownSemanticSpanKind.InlineMath or MarkdownSemanticSpanKind.BlockMath;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
