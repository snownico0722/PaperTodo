using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;
using Renderer = PaperTodo.MarkdownEdgeCapsulePreviewRenderer;

internal static partial class Program
{
    private static void ArtifactRenderingChecks()
    {
        // Source admission and geometry remain independent of the cache and of the drawing engine.
        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic,
            MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        {
            var sixteen = string.Join('\n', Enumerable.Range(1, 16).Select(i => $"row {i}"));
            var admitted = Renderer.CaptureContent(sixteen, mode);
            var tail = Renderer.CaptureContent(sixteen + "\nignored", mode);
            Require(!admitted.Truncated && tail.Truncated && admitted.Lines.SequenceEqual(tail.Lines), "16-block budget admits the same visible source");
            Require(Renderer.MeasureText(admitted) == Renderer.MeasureText(tail) &&
                Renderer.EstimateVisualLines(admitted, 200) == Renderer.EstimateVisualLines(tail, 200), "off-budget text never affects card geometry");
            var limit = Renderer.CaptureContent(new string('文', 6000), mode);
            Require(!limit.Truncated && Renderer.CaptureContent(new string('文', 6001), mode).Truncated, "strict 6000-character budget");
            var panel = new StackPanel();
            Require(RenderForCheck(panel, new string('文', 6000), _ => { }, mode, new Size(150, 80)), "bounded layout reports its invisible tail");
            var surface = panel.Children.OfType<MarkdownPreviewArtifactSurface>().Single();
            var before = surface.Artifact;
            panel.Measure(new Size(150, 80)); panel.Arrange(new Rect(0, 0, 150, 80));
            panel.InvalidateMeasure(); panel.UpdateLayout();
            Require(ReferenceEquals(before, surface.Artifact) && !Elements(surface).Any(x => x is TextBlock), "measure/repaint never recreate text blocks or layout");
            Require(PreviewText(surface).Length < 1000 && surface.Artifact.ContentHeight < 200, "cold formatter stops at the visible boundary");
        }
        var code = "```\n" + string.Join('\n', Enumerable.Repeat("literal **code**", 40)) + "\n```";
        Require(!Renderer.CaptureContent(code, MarkdownRenderModes.Full).Truncated, "a fenced code block uses one block slot");
        Require(Renderer.CaptureContent(code, MarkdownRenderModes.Enhanced).Truncated, "source modes count individual admitted lines");
        var marker = Renderer.CaptureContent("# title\n> quote\n- item\n- [x] done\n12) item\n---\n![image](i:123456)\n" + code, MarkdownRenderModes.Full);
        var root = new StackPanel();
        var plan = Renderer.CaptureArtifactPlan(root, marker, 400, 1);
        Require(plan.Blocks.Select(b => b.Kind).Distinct().Count() == 6, "finite text/quote/list/rule/image/code vocabulary is covered");
        Require(plan.Styles.All(s => s.Foreground.IsFrozen && (s.Background?.IsFrozen ?? true)), "layout snapshot carries only frozen resources");
        // Underline belongs to its enclosing Span/Hyperlink, not faded syntax Runs.
        root.Resources["TextBrushKey"] = Brushes.Black;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        foreach (var (text, color) in new[] {
            ("<u>under ~~strike~~</u>", Colors.Black),
            ("[a **bold**](https://example.com)", Colors.Blue) })
        {
            var syntaxPlan = Renderer.CaptureArtifactPlan(root,
                Renderer.CaptureContent(text, MarkdownRenderModes.Enhanced), 400, 1);
            var decoratedSyntax = syntaxPlan.Styles.Where(s =>
                s.Foreground is SolidColorBrush b && b.Color == ((SolidColorBrush)Theme.SyntaxFadeBrush).Color &&
                s.Decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true).ToArray();
            Require(decoratedSyntax.Length > 0 && decoratedSyntax.All(s => s.Decorations!
                .Where(d => d.Location == TextDecorationLocation.Underline)
                .All(d => d.Pen?.Brush is SolidColorBrush b && b.Color == color)),
                "nested faded syntax retains the enclosing decoration color");
        }
        IndependentDrawingReferences();
        Console.WriteLine("PASS artifact budgets, clipping, empty lines, complete block vocabulary and independent drawing references");
    }

    private static void IndependentDrawingReferences()
    {
        // Hand-authored expected presentation, NOT the artifact builder used twice and NOT a
        // retained legacy Markdown parser. Small explicit fixtures keep the visual contract readable.
        var cases = 0;
        try
        {
            foreach (var sharp in new[] { false, true })
            foreach (var zoom in new[] { 0.7, 1.3 })
            {
                AppTypography.Configure(sharp ? UiFontPresets.YaHei : UiFontPresets.Default,
                    textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                TextBlock Text(string value, double? size = null, bool weak = false, bool code = false, bool strong = false)
                {
                    var block = new TextBlock
                    {
                        Text = value, TextWrapping = TextWrapping.Wrap,
                        FontFamily = code ? NoteTypography.CodeFontFamily : strong ? AppTypography.FontFamilyFor(true, true) : NoteTypography.FontFamily,
                        FontSize = Math.Round((size ?? (code ? NoteTypography.CodeFontSize : NoteTypography.FontSize)) * zoom, 1),
                        FontStyle = NoteTypography.FontStyle, FontStretch = NoteTypography.FontStretch,
                        FontWeight = strong ? NoteTypography.HeadingFontWeight : NoteTypography.FontWeight,
                        Foreground = weak ? Brushes.Gray : Brushes.Black, Language = NoteTypography.Language
                    };
                    NoteTypography.ApplyTextRendering(block);
                    return block;
                }
                StackPanel Rows(params UIElement[] rows)
                {
                    var panel = new StackPanel();
                    foreach (var row in rows) panel.Children.Add(row);
                    return panel;
                }
                FrameworkElement List(string marker, bool done)
                {
                    var grid = new Grid { Margin = new Thickness(2, 0, 0, 0) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    var mark = Text(marker, weak: true); mark.Margin = new Thickness(0, 0, AppTypography.Scale(6), 0);
                    var body = Text("item", weak: done); if (done) body.TextDecorations = TextDecorations.Strikethrough;
                    Grid.SetColumn(body, 1); grid.Children.Add(mark); grid.Children.Add(body);
                    return grid;
                }
                FrameworkElement Rule()
                {
                    var grid = new Grid();
                    var text = Text("---"); text.Foreground = Brushes.Transparent;
                    grid.Children.Add(text);
                    grid.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 2, 0), Background = Brushes.Gray });
                    return grid;
                }
                var references = new (string Source, Func<FrameworkElement> Expected)[]
                {
                    ("ordinary 中文 e\u0301 العربية 😀", () => Text("ordinary 中文 e\u0301 العربية 😀")),
                    ("a\n\nb", () => Rows(Text("a"), Text(""), Text("b"))),
                    ("## Heading", () => Text("Heading", NoteTypography.Heading2FontSize, strong: true)),
                    ("> quote", () => new Border { Margin = new Thickness(4,0,0,0), Padding = new Thickness(8,0,5,0), Child = Text("quote", weak: true) }),
                    ("12) item", () => List("12.", false)),
                    ("- item", () => List("•", false)),
                    ("- [x] item", () => List("☑", true)),
                    ("---", Rule),
                    ("![image](i:123456)", () => new Border { Margin = new Thickness(1,4,1,4), Padding = new Thickness(8,7,8,7),
                        CornerRadius = new CornerRadius(5), Background = Brushes.LightGray, Child = Text("▧ image", AppTypography.Scale(11.5), weak: true) }),
                    ("```\n\nliteral **code**\n\n```", () => new Border { Background = Brushes.LightGray,
                        Child = Rows(Text("\u200B", code: true), Text("literal **code**", code: true), Text("\u200B", code: true)) }),
                    ("", () => { var t = Text("—", AppTypography.Scale(16), weak: true); t.FontSize = AppTypography.Scale(16);
                        t.Margin = new Thickness(4,18,4,4); t.HorizontalAlignment = HorizontalAlignment.Center; return t; })
                };
                foreach (var fixture in references)
                {
                    var expected = Rows(fixture.Expected());
                    var actual = new StackPanel();
                    foreach (var panel in new[] { expected, actual })
                    {
                        panel.Width = 360; panel.Height = 240; panel.Background = Brushes.White; panel.ClipToBounds = true;
                        panel.Resources["TextBrushKey"] = Brushes.Black; panel.Resources["WeakTextBrushKey"] = Brushes.Gray;
                        panel.Resources["HoverBrushKey"] = Brushes.LightGray; panel.Resources["PaperBorderBrushKey"] = Brushes.Gray;
                    }
                    RenderForCheck(actual, fixture.Source, _ => { }, MarkdownRenderModes.Full, new Size(360, 240), zoom);
                    byte[] Pixels(FrameworkElement element)
                    {
                        element.Measure(new Size(360,240)); element.Arrange(new Rect(0,0,360,240)); element.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(360,240,96,96,PixelFormats.Pbgra32); bitmap.Render(element);
                        var bytes = new byte[360*240*4]; bitmap.CopyPixels(bytes, 360*4, 0); return bytes;
                    }
                    var reference = Pixels(expected); var result = Pixels(actual);
                    Require(result.Zip(reference).All(pair => Math.Abs(pair.First - pair.Second) <= 32),
                        $"independent WPF reference: {sharp}/{zoom}/{fixture.Source}");
                    cases++;
                }
            }
        }
        finally { AppTypography.Configure(UiFontPresets.Default); }
        Console.WriteLine($"INDEPENDENT_ARTIFACT_PIXELS cases={cases}");
    }
}
