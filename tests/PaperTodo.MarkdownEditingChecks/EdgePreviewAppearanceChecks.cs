using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void RunEdgePreviewAppearanceChecks(Action<string, Action> check)
    {
        check("Edge note typography and natural line spacing match the note body", () =>
        {
            try
            {
                foreach (var larger in new[] { false, true })
                {
                    AppTypography.Configure(larger ? UiFontPresets.YaHei : UiFontPresets.Default,
                        larger ? 1.2 : 1.0, textRenderingProfile: larger ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                    NoteTypography.Configure(larger ? VisualTextSizes.Large : VisualTextSizes.Medium, larger);
                    foreach (var zoom in new[] { 1.0, 1.3 })
                    foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
                    {
                        const string source = "第一行 ABC\n第二行 abc\n\n最后一行";
                        using var editor = new Editor(source);
                        editor.Box.SetTextZoom(zoom);
                        editor.Box.SetMarkdownRenderMode(mode);
                        editor.Box.SetPreviewMode(true);
                        editor.Box.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        editor.Box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        var panel = new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };
                        var host = new Grid();
                        host.ColumnDefinitions.Add(new ColumnDefinition());
                        host.ColumnDefinitions.Add(new ColumnDefinition());
                        host.Children.Add(editor.Box);
                        Grid.SetColumn(panel, 1);
                        host.Children.Add(panel);
                        var window = new Window { Content = host, Width = 900, Height = 650, ShowInTaskbar = false };
                        try
                        {
                            // Attach the editor's template before comparing inherited typography.
                            // Both surfaces use the same real window/DPI and equal text widths.
                            window.Show();
                            Pump();
                            var textView = editor.Box.TextArea.TextView;
                            Equal(editor.Box.FontSize, (double)textView.GetValue(Control.FontSizeProperty),
                                "reference view receives the editor's font size");
                            Require(textView.ActualWidth > 100, "reference text lane has a real layout");
                            panel.Width = textView.ActualWidth;
                            foreach (var sample in new[] { source, new string('文', 75) + " ABC\n\n最后一行" })
                            {
                                editor.Box.Text = sample;
                                MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, sample, _ => { }, mode, textZoom: zoom);
                                Pump();
                                host.UpdateLayout();
                                textView.EnsureVisualLines();
                                Equal(editor.Box.Document.LineCount, textView.VisualLines.Count, "all reference source lines are visible");
                                var noteHeight = textView.VisualLines.Sum(line => line.Height);
                                Console.WriteLine($"  Edge line metrics {mode} large={larger} zoom={zoom}: note={noteHeight:F3}, preview={panel.DesiredSize.Height:F3}");
                                Require(Math.Abs(noteHeight - panel.DesiredSize.Height) <= 2,
                                    "natural lines, wrapping and empty lines match the note within pixel rounding");
                                foreach (var text in panel.Children.OfType<TextBlock>())
                                {
                                    Equal(editor.Box.FontSize, text.FontSize, "body size includes the same paper zoom");
                                    Equal(editor.Box.FontFamily.Source, text.FontFamily.Source, "same content font");
                                    Equal(editor.Box.FontWeight, text.FontWeight, "same body weight");
                                    Equal(editor.Box.Language, text.Language, "same language fallback");
                                    Equal(TextOptions.GetTextFormattingMode(editor.Box), TextOptions.GetTextFormattingMode(text), "same text formatting profile");
                                    Require(double.IsNaN(text.LineHeight) && text.Margin.Top == 0 && text.Margin.Bottom == 0,
                                        "no additional preview line height or paragraph gap");
                                }
                            }

                            MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, "## 标题\n`code` **strong**", _ => { }, mode, textZoom: zoom);
                            var heading = (TextBlock)panel.Children[0];
                            Equal(Math.Round((mode == MarkdownRenderModes.Off ? NoteTypography.FontSize : NoteTypography.Heading2FontSize) * zoom, 1),
                                heading.FontSize, "heading uses the note's level-specific size");
                            if (mode != MarkdownRenderModes.Off)
                            {
                                var expectedWeight = AppTypography.UsesCustomBoldFace(true)
                                    ? AppTypography.FontWeightFor(true) : NoteTypography.HeadingFontWeight;
                                Equal(expectedWeight, heading.FontWeight, "same heading weight");
                                var row = (TextBlock)panel.Children[1];
                                var bold = row.Inlines.OfType<Bold>().Single();
                                Equal(expectedWeight, bold.FontWeight, "same semantic strong weight");
                                Equal(AppTypography.FontFamilyFor(content: true, bold: true).Source, bold.FontFamily.Source, "same semantic bold face");
                                var code = row.Inlines.OfType<Span>().First(span => span is not Bold);
                                Equal(NoteTypography.CodeFontFamily.Source, code.FontFamily.Source, "same inline code family");
                                Equal(Math.Round(NoteTypography.CodeFontSize * zoom, 1), code.FontSize, "inline code zoom is composed once");
                            }
                        }
                        finally { window.Close(); Pump(); }
                    }
                }
            }
            finally
            {
                AppTypography.Configure(UiFontPresets.Default);
                NoteTypography.Configure(VisualTextSizes.Medium, false);
            }
        });

        check("Open edge note preview follows per-paper text zoom", () =>
        {
            var paper = new PaperData { TextZoom = 1.3 };
            var invalidation = new EdgeCapsulePreviewInvalidationSource();
            var context = new EdgeCapsulePreviewContext(
                paper, () => "笔记", false, () => "正文", () => MarkdownRenderModes.Full,
                (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, invalidation);
            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
            view.PrepareForFirstDisplay();
            var window = new Window { Content = view, Width = 460, Height = 410, ShowInTaskbar = false };
            try
            {
                window.Show();
                foreach (var zoom in new[] { 1.3, 0.8 })
                {
                    paper.TextZoom = zoom;
                    invalidation.Invalidate();
                    Pump();
                    var viewport = view.Children.OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
                    var body = viewport.Children.OfType<StackPanel>().Single();
                    Equal(Math.Round(NoteTypography.FontSize * zoom, 1), ((TextBlock)body.Children[0]).FontSize,
                        "live preview reads the paper's current text zoom");
                }
            }
            finally { window.Close(); Pump(); }
        });

        check("Edge todo excerpt cannot scroll and keeps visible actions", () =>
        {
            var paper = new PaperData { Type = PaperTypes.Todo };
            for (var i = 0; i < 30; i++)
                paper.Items.Add(new PaperItem { Id = $"item-{i}", Text = $"待办 {i}", Order = i });
            paper.Items[0].LinkPaper("linked-paper");
            var invalidation = new EdgeCapsulePreviewInvalidationSource();
            string? toggled = null;
            string? opened = null;
            var context = new EdgeCapsulePreviewContext(
                paper, () => "待办", false, () => "", () => MarkdownRenderModes.Off,
                (id, done) =>
                {
                    toggled = id;
                    paper.Items.Single(item => item.Id == id).Done = done;
                    invalidation.Invalidate();
                    return true;
                },
                id => { opened = id; return true; },
                () => new Style(typeof(CheckBox)), () => "", _ => { }, invalidation);
            var descriptor = TodoEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
            view.PrepareForFirstDisplay();
            var window = new Window { Content = view, Width = 450, Height = 180, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                var viewport = view.Children.OfType<TodoEdgeCapsulePreviewViewport>().Single();
                var items = viewport.Children.OfType<StackPanel>().Single();
                var indicator = viewport.Children.OfType<TextBlock>().Single();
                Require(!EdgePreviewElements(view).Any(element => element is ScrollViewer or ScrollBar),
                    "todo has no scrolling control, not merely hidden scrollbars");
                Equal(TodoEdgeCapsulePreviewProvider.MaximumRenderedItems, items.Children.Count, "row creation stays bounded");
                Equal(1.0, indicator.Opacity, "overflow is indicated");
                var first = (FrameworkElement)items.Children[0];
                var firstTop = first.TranslatePoint(new Point(), viewport);
                var clip = items.Clip.Bounds;
                foreach (var delta in new[] { -120, 120 })
                {
                    first.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                        { RoutedEvent = Mouse.MouseWheelEvent });
                    Pump();
                    Equal(firstTop, first.TranslatePoint(new Point(), viewport), "wheel cannot move todo rows");
                    Equal(clip, items.Clip.Bounds, "wheel cannot reveal more rows");
                }
                var last = (FrameworkElement)items.Children[^1];
                last.BringIntoView();
                Pump();
                Equal(firstTop, first.TranslatePoint(new Point(), viewport), "BringIntoView cannot scroll the excerpt");
                var hidden = EdgePreviewElements(last).OfType<CheckBox>().Single();
                var hiddenPoint = hidden.TranslatePoint(new Point(10, 10), viewport);
                Require(hiddenPoint.Y >= clip.Bottom && viewport.InputHitTest(hiddenPoint) == null,
                    "clipped-away checkbox cannot receive pointer input");

                var checkbox = EdgePreviewElements(first).OfType<CheckBox>().Single();
                checkbox.IsChecked = true;
                checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Equal("item-0", toggled, "visible checkbox keeps the original mutation callback");
                Require(paper.Items[0].Done, "checkbox still changes completion");
                var link = EdgePreviewElements(items.Children[0]).OfType<Button>().Single();
                Require(EdgeCapsulePreviewInteraction.GetConsumesPointer(link), "link keeps its independent input region");
                link.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Equal("item-0", opened, "visible association action still works");

                window.Height = 600;
                Pump();
                Require(items.DesiredSize.Height < viewport.ActualHeight, "all realized rows now fit");
                Equal(1.0, indicator.Opacity, "unrealized source beyond the row budget still shows overflow");
                while (paper.Items.Count > 2) paper.Items.RemoveAt(paper.Items.Count - 1);
                invalidation.Invalidate();
                Pump();
                Equal(0.0, indicator.Opacity, "fitting short list clears overflow");
                window.Height = 180;
                paper.Items[0].Text = new string('文', 500);
                invalidation.Invalidate();
                Pump();
                Equal(1.0, indicator.Opacity, "a long wrapped item shows geometric overflow even below the row budget");
                paper.Items.Clear();
                invalidation.Invalidate();
                Pump();
                Equal(0.0, indicator.Opacity, "empty list clears stale overflow");
            }
            finally { window.Close(); Pump(); }
        });
    }
}
