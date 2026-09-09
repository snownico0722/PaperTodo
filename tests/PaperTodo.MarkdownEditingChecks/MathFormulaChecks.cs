using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static partial class Program
{
    private static void RunMathFormulaChecks(Action<string, Action> check)
    {
        check("Math semantics recognize all four delimiter pairs", () =>
        {
            AssertMath("中文$x^2$公式", MarkdownSemanticSpanKind.InlineMath, 1, "x^2", false);
            AssertMath(@"before \(x+y\) after", MarkdownSemanticSpanKind.InlineMath, 2, "x+y", false);
            AssertMath(
                "$$\n\\frac{a+b}{c+d}\n$$",
                MarkdownSemanticSpanKind.BlockMath,
                2,
                "\\frac{a+b}{c+d}",
                true);
            AssertMath(
                "\\[\n\\begin{pmatrix}a & b \\\\ c & d\\end{pmatrix}\n\\]",
                MarkdownSemanticSpanKind.BlockMath,
                2,
                "\\begin{pmatrix}a & b \\\\ c & d\\end{pmatrix}",
                true);

            var multiple = MathSpans(MarkdownSemanticSnapshot.Parse("$x$加$y$"));
            Equal(2, multiple.Length, "multiple inline formulas share one line");
        });

        check("Math semantics exclude code, escaped dollars and ordinary currency", () =>
        {
            var source = "`$x$`\n\n```text\n$$\nx+y\n$$\n```\n\n\\$x$ and $100 and $200";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            Equal(0, MathSpans(snapshot).Length, "protected Markdown and currency stay source");
        });

        check("Math owns TeX-shaped Markdown but not an outer Markdown link", () =>
        {
            var formulaSource = "$https://example.com/a_b$";
            var formula = MarkdownSemanticSnapshot.Parse(formulaSource);
            var math = MathSpans(formula);
            Equal(1, math.Length, "URL-shaped TeX is a formula");
            Equal(0, formula.Links.Count, "URL-shaped TeX is not clickable");
            Require(
                !formula.Spans.Any(span =>
                    span.Start >= math[0].Start &&
                    span.End <= math[0].End &&
                    span.Kind != MarkdownSemanticSpanKind.InlineMath),
                "formula removes accidental nested Markdown presentation");

            var linkSource = "[label $x$](https://example.com)";
            var link = MarkdownSemanticSnapshot.Parse(linkSource);
            Equal(0, MathSpans(link).Length, "formula delimiter inside a link label is not substituted");
            Equal(1, link.Links.Count, "outer Markdown link remains authoritative");
        });

        check("Unclosed, empty and oversized math delimiters stay literal source", () =>
        {
            foreach (var source in new[] { "$x", "$$\nx", "\\(x", "\\[x", "$$  $$", "$ $" })
            {
                Equal(0, MathSpans(MarkdownSemanticSnapshot.Parse(source)).Length, source);
            }

            var oversized = "$" +
                new string('x', MarkdownMathScanner.MaximumFormulaContentLength + 1) +
                "$";
            Equal(0, MathSpans(MarkdownSemanticSnapshot.Parse(oversized)).Length,
                "oversized formula is not turned into a giant semantic range");
        });

        check("Math delimiter edits decline local parsing while body edits stay incremental", () =>
        {
            var prefix = new string('a', 2_400) + "\n";
            var suffix = "\n" + new string('b', 2_400);
            var oldSource = prefix + "$x^2$" + suffix;
            var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
            var bodyChanged = oldSource.Replace("x^2", "x^3", StringComparison.Ordinal);
            Require(
                MarkdownSemanticSnapshot.TryParseIncrementalLocal(
                    oldSource,
                    oldSnapshot,
                    bodyChanged,
                    out var bodySnapshot,
                    out _),
                "editing only a formula body remains local");
            var bodyMath = MathSpans(bodySnapshot);
            Equal(1, bodyMath.Length, "incremental body edit keeps one formula");
            Require(
                MarkdownMathSource.TryExtract(bodyChanged, bodyMath[0], out var formula, out _),
                "incremental formula source extracts");
            Equal("x^3", formula, "incremental formula content updates");

            var closingDollar = oldSource.IndexOf("$" + suffix, StringComparison.Ordinal);
            var delimiterChanged = oldSource.Remove(closingDollar, 1);
            Require(
                !MarkdownSemanticSnapshot.TryParseIncrementalLocal(
                    oldSource,
                    oldSnapshot,
                    delimiterChanged,
                    out _,
                    out _),
                "removing a delimiter forces a full parse");

            var newlineChanged = oldSource.Insert(prefix.Length + 2, "\n");
            Require(
                !MarkdownSemanticSnapshot.TryParseIncrementalLocal(
                    oldSource,
                    oldSnapshot,
                    newlineChanged,
                    out _,
                    out _),
                "splitting inline dollar math forces a full parse before distant re-pairing");

            // The delimiter character can stay untouched while its escape state changes. With two
            // backslashes the nearby dollar closes the old formula; deleting one makes that dollar
            // escaped and allows the opener to pair with the distant dollar instead. The distant
            // close sits beyond the normal local window, so this must decline incremental parsing.
            var escapeSource = prefix + "$x\\\\$" + new string('c', 1_800) + "$" + suffix;
            var escapeSnapshot = MarkdownSemanticSnapshot.Parse(escapeSource);
            var escapeRun = escapeSource.IndexOf("\\\\$", prefix.Length, StringComparison.Ordinal);
            var escapeChanged = escapeSource.Remove(escapeRun, 1);
            Require(
                !MarkdownSemanticSnapshot.TryParseIncrementalLocal(
                    escapeSource,
                    escapeSnapshot,
                    escapeChanged,
                    out _,
                    out _),
                "changing backslash parity before a dollar forces a full parse");
        });

        check("WpfMath renders common inline and multiline structures", () =>
        {
            foreach (var formula in new[]
            {
                @"x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}",
                @"\sum_{i=1}^{n} i^2",
                @"\left\{x\right.",
                @"\begin{aligned}a&=b+c\\d&=e+f\end{aligned}",
                @"\begin{matrix}a&b\\c&d\end{matrix}",
                @"\begin{pmatrix}a&b\\c&d\end{pmatrix}",
                @"\begin{cases}x+y=2\\x-y=1\end{cases}"
            })
            {
                Require(
                    MarkdownMathRenderer.TryRender(
                        formula,
                        display: true,
                        fontSize: 16,
                        Brushes.Black,
                        "Arial",
                        out var drawing),
                    $"WpfMath renders {formula}");
                Require(drawing.Width > 0 && drawing.Height > 0, "formula has positive geometry");
                Require(drawing.Drawing.IsFrozen, "cached formula drawing is immutable");
                Require(
                    drawing.Baseline >= 0 && drawing.Baseline <= drawing.Height,
                    "formula baseline stays inside the element");
            }
        });

        check("Invalid math falls back to source instead of throwing", () =>
        {
            Require(
                !MarkdownMathRenderer.TryRender(
                    @"\frac{",
                    display: false,
                    fontSize: 16,
                    Brushes.Black,
                    "Arial",
                    out _),
                "invalid TeX is rejected");
        });

        check("Preview and Full mode replace and reveal inline math", () =>
        {
            const string source = "A $\\frac{a}{b}$ B\nplain";
            using var editor = new Editor(source);
            var box = editor.Box;
            var span = MathSpans(MarkdownSemanticSnapshot.Parse(source)).Single();

            box.SetMarkdownRenderMode(MarkdownRenderModes.Enhanced);
            box.SetPreviewMode(false);
            Pump();
            LayoutMathEditor(box);
            Require(!HasMathElement(box, span), "ordinary Enhanced editing keeps formula source");

            box.SetPreviewMode(true);
            Pump();
            LayoutMathEditor(box);
            var rendered = GetMathElement(box, span);
            var baseline = TextBlock.GetBaselineOffset(rendered.Element);
            Require(double.IsFinite(baseline) && baseline > 0, "inline formula publishes a baseline");
            Require(rendered.Element.DesiredSize.Height > 0, "inline formula participates in line height");

            box.SetPreviewMode(false);
            box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            box.CaretOffset = source.Length;
            Pump();
            LayoutMathEditor(box);
            Require(HasMathElement(box, span), "Full editing renders a formula outside the caret range");

            box.CaretOffset = span.Start + span.MarkerLength;
            Pump();
            LayoutMathEditor(box);
            Require(!HasMathElement(box, span), "caret inside formula restores exact Markdown source");
        });

        check("Full preview folds one cross-line formula into one layout element", () =>
        {
            const string source = "before\n$$\nx^2 + y^2\n$$\nafter";
            using var editor = new Editor(source);
            var box = editor.Box;
            box.SetPreviewMode(true);
            Pump();
            LayoutMathEditor(box);

            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var span = MathSpans(snapshot).Single();
            var openingLine = box.Document.GetLineByOffset(span.Start);
            var visual = box.TextArea.TextView.GetOrConstructVisualLine(openingLine);
            var element = visual.Elements.OfType<InlineObjectElement>().Single(candidate =>
                candidate.RelativeTextOffset == span.Start - visual.FirstDocumentLine.Offset);
            Equal(span.Length, element.DocumentLength,
                "one inline object consumes the complete multi-line source");
            Require(
                visual.LastDocumentLine.LineNumber >= box.Document.GetLineByOffset(span.End - 1).LineNumber,
                "the visual line owns every physical line in the formula");
            Equal(source, box.Text, "rendering never rewrites the Markdown source");
            Require(!box.CanUndo, "rendering adds no undo operation");

            box.SetPreviewMode(false);
            box.CaretOffset = span.Start + span.MarkerLength;
            Pump();
            LayoutMathEditor(box);
            visual = box.TextArea.TextView.GetOrConstructVisualLine(openingLine);
            Require(
                !visual.Elements.OfType<InlineObjectElement>().Any(candidate =>
                    candidate.RelativeTextOffset == span.Start - visual.FirstDocumentLine.Offset),
                "placing the caret inside the formula reveals the complete source");

            box.CaretOffset = box.Text.Length;
            Pump();
            LayoutMathEditor(box);
            visual = box.TextArea.TextView.GetOrConstructVisualLine(openingLine);
            Require(
                visual.Elements.OfType<InlineObjectElement>().Any(candidate =>
                    candidate.RelativeTextOffset == span.Start - visual.FirstDocumentLine.Offset),
                "leaving the formula restores the rendered element");
        });

        check("Resizing multiline math invalidates stale visual lines before fold state changes", () =>
        {
            var longFormula = string.Join("+", Enumerable.Repeat("x", 50));
            var source = $"before\n$$\n{longFormula}\n$$\nafter";
            using var editor = new Editor(source);
            var box = editor.Box;
            box.SetPreviewMode(true);
            Pump();

            var span = MathSpans(MarkdownSemanticSnapshot.Parse(source)).Single();

            // At a very narrow width the formula deliberately remains source because rendering it
            // would require scaling below the readability floor. This constructs normal VisualLines
            // for every physical source row.
            LayoutMathEditor(box, 96);
            Pump();
            Require(!HasMathElement(box, span), "narrow preview keeps oversized formula source");

            // Widening queues the fold sync until the current WPF layout has settled. It must then
            // discard the old source VisualLines before collapsing continuation lines; otherwise
            // AvalonEdit can throw "Trying to build visual line from collapsed line" on re-measure.
            LayoutMathEditor(box, 800);
            Pump();
            Require(HasMathElement(box, span), "widening safely folds and renders the formula");

            // Exercise the inverse transition as well: uncollapse back to source, then fold again.
            LayoutMathEditor(box, 96);
            Pump();
            Require(!HasMathElement(box, span), "narrowing safely restores formula source");
            LayoutMathEditor(box, 800);
            Pump();
            Require(HasMathElement(box, span), "re-widening safely renders the formula again");
        });

        check("Math scanner stays cheap on a 100k note", () =>
        {
            var source = new string('a', 49_000) + "$x^2$" + new string('b', 49_000);
            var stopwatch = Stopwatch.StartNew();
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            stopwatch.Stop();
            Equal(1, MathSpans(snapshot).Length, "large note math count");
            Require(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"large-note parse exceeded smoke budget: {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        });
    }

    private static void AssertMath(
        string source,
        MarkdownSemanticSpanKind kind,
        int markerLength,
        string expectedFormula,
        bool expectedDisplay)
    {
        var spans = MathSpans(MarkdownSemanticSnapshot.Parse(source));
        Equal(1, spans.Length, "math span count");
        var span = spans[0];
        Equal(kind, span.Kind, "math kind");
        Equal(markerLength, span.MarkerLength, "delimiter length");
        Require(
            MarkdownMathSource.TryExtract(source, span, out var formula, out var display),
            "formula source extracts");
        Equal(expectedFormula, formula, "formula content");
        Equal(expectedDisplay, display, "display mode");
    }

    private static MarkdownSemanticSpan[] MathSpans(MarkdownSemanticSnapshot snapshot) =>
        snapshot.Spans
            .Where(span => span.Kind is MarkdownSemanticSpanKind.InlineMath or MarkdownSemanticSpanKind.BlockMath)
            .ToArray();

    private static bool HasMathElement(
        MarkdownTextBox box,
        MarkdownSemanticSpan span)
    {
        try
        {
            _ = GetMathElement(box, span);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static InlineObjectElement GetMathElement(
        MarkdownTextBox box,
        MarkdownSemanticSpan span)
    {
        var line = box.Document.GetLineByOffset(span.Start);
        var visual = box.TextArea.TextView.GetOrConstructVisualLine(line);
        return visual.Elements.OfType<InlineObjectElement>().Single(candidate =>
            candidate.RelativeTextOffset == span.Start - visual.FirstDocumentLine.Offset);
    }

    private static void LayoutMathEditor(
        MarkdownTextBox box,
        double width = 800,
        double height = 600)
    {
        box.ApplyTemplate();
        var size = new Size(width, height);
        box.Measure(size);
        box.Arrange(new Rect(0, 0, width, height));
        box.UpdateLayout();
        var view = box.TextArea.TextView;
        view.Measure(size);
        view.Arrange(new Rect(0, 0, width, height));
        view.EnsureVisualLines();
    }
}
