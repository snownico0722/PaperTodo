using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void RunEdgePreviewChecks(Action<string, Action> check)
    {
        check("Edge note preview follows all four Markdown modes", () =>
        {
            const string source = "## 标题\n  - [x] **完成**\n> *引用*\n[链接](https://example.com)\n![图片](i:asset)\n\\*原文\\*\n```md\n**代码**\n```";
            var panel = new StackPanel();
            foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced })
            {
                MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, source, _ => { }, mode);
                Equal(source, EdgePreviewText(panel), $"{mode} retains source syntax and indentation");
                Equal(0, ((TextBlock)panel.Children[7]).Inlines.OfType<Span>().Count(), "fenced code stays literal");
            }

            MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, "**粗体**", _ => { }, MarkdownRenderModes.Off);
            Require(!((TextBlock)panel.Children[0]).Inlines.OfType<Bold>().Any(), "Off does not style Markdown");
            foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced })
            {
                MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, "**粗体**", _ => { }, mode);
                var text = (TextBlock)panel.Children[0];
                Require(text.Inlines.OfType<Bold>().Any(), "enabled modes style emphasis");
                var marker = text.Inlines.OfType<Run>().First();
                Equal(mode == MarkdownRenderModes.Enhanced,
                    marker.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue,
                    "only Enhanced fades syntax");
            }

            MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, "## 标题\n**粗体**", _ => { }, MarkdownRenderModes.Full);
            Equal("标题\n粗体", EdgePreviewText(panel), "Full removes visible heading and emphasis syntax");
        });

        check("Edge note preview keeps Enhanced list markers readable and renders rules", () =>
        {
            var panel = new StackPanel();
            foreach (var source in new[] { "  - **条目**", "  + **条目**", "  * **条目**", "  12. **条目**", "  - [x] **条目**" })
            {
                foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced })
                {
                    MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, source, _ => { }, mode);
                    var bullet = mode == MarkdownRenderModes.Enhanced && !source.Contains("[x]") && !source.Contains("12.");
                    Equal(bullet ? "  • **条目**" : source, EdgePreviewText(panel), "marker respects mode and keeps indentation");
                    var row = (TextBlock)panel.Children[0];
                    Require(row.Inlines.FirstInline.Foreground is SolidColorBrush brush && brush.Color.A == 255,
                        "visible bullet, number or task marker is not faded");
                    if (mode == MarkdownRenderModes.Enhanced)
                    {
                        var syntax = row.Inlines.OfType<Run>().First(run => run.Text == "**");
                        Require(syntax.Foreground is SolidColorBrush faded && faded.Color.A < 255,
                            "inline emphasis syntax still fades independently of the list marker");
                    }
                }
            }

            foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            {
                MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, "---", _ => { }, mode);
                var rule = panel.Children[0] as Border ?? (panel.Children[0] as Grid)?.Children.OfType<Border>().Single();
                Equal(mode != MarkdownRenderModes.Off, rule != null, "only enabled modes draw a rule");
                if (rule != null)
                {
                    panel.Measure(new Size(300, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, 300, panel.DesiredSize.Height));
                    Require(rule.ActualWidth > 200, "rule spans the available row width");
                }
                if (mode is MarkdownRenderModes.Basic or MarkdownRenderModes.Enhanced)
                {
                    var sourceText = ((Grid)panel.Children[0]).Children.OfType<TextBlock>().Single();
                    var alpha = ((SolidColorBrush)sourceText.Foreground).Color.A;
                    Equal(mode == MarkdownRenderModes.Enhanced ? (byte)0 : (byte)255, alpha,
                        "Basic keeps source visible; Enhanced replaces its visible markers");
                }
            }
        });

        check("Open edge note preview refreshes after the render setting changes", () =>
        {
            var mode = MarkdownRenderModes.Off;
            var invalidation = new EdgeCapsulePreviewInvalidationSource();
            var context = new EdgeCapsulePreviewContext(
                new PaperData(), () => "笔记", false, () => "**内容**", () => mode,
                (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, invalidation);
            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
            view.PrepareForFirstDisplay();
            var window = new Window { Content = view, Width = 460, Height = 410, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                Require(EdgePreviewText(view).Contains("**内容**"), "initial preview uses Off");
                mode = MarkdownRenderModes.Full;
                invalidation.Invalidate();
                Pump();
                var displayed = EdgePreviewText(view);
                Require(displayed.Contains("内容") && !displayed.Contains("**"), "open preview reads current mode on refresh");
            }
            finally
            {
                window.Close();
                Pump();
            }
        });

        check("Edge note preview clips overflow without scrolling and fills available space", () =>
        {
            var source = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"正文 {i}"));
            var mode = MarkdownRenderModes.Off;
            var invalidation = new EdgeCapsulePreviewInvalidationSource();
            var context = new EdgeCapsulePreviewContext(
                new PaperData(), () => "笔记", false, () => source, () => mode,
                (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, invalidation);
            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
            view.PrepareForFirstDisplay();
            var window = new Window { Content = view, Width = 460, Height = 410, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                var viewport = view.Children.OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
                var body = viewport.Children.OfType<StackPanel>().Single();
                var indicator = viewport.Children.OfType<TextBlock>().Single();
                foreach (var renderMode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
                {
                    mode = renderMode;
                    invalidation.Invalidate();
                    Pump();
                    body = viewport.Children.OfType<StackPanel>().Single();
                    Require(!EdgePreviewElements(view).OfType<ScrollViewer>().Any(), "note preview has no scrolling surface in any mode");
                    var clip = body.Clip.Bounds;
                    var last = (FrameworkElement)body.Children[^1];
                    Require(last.TranslatePoint(new Point(0, last.ActualHeight), viewport).Y >= clip.Bottom,
                        "realized content reaches the bottom of the visible excerpt");
                    Require(body.Children.Count < 40, "invisible tail is not constructed");
                    Equal(1.0, indicator.Opacity, "overflow shows an ellipsis");
                    Require(indicator.TranslatePoint(new Point(), viewport).Y >= clip.Bottom, "ellipsis does not cover visible text");
                    var first = (FrameworkElement)body.Children[0];
                    var top = first.TranslatePoint(new Point(), viewport);
                    viewport.InvalidateMeasure();
                    Pump();
                    Require(ReferenceEquals(body, viewport.Children.OfType<StackPanel>().Single()), "same-size layout reuses the published tree");
                    Require(ReferenceEquals(first, body.Children[0]), "same-size layout reuses the rendered excerpt");
                    first.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    {
                        RoutedEvent = Mouse.MouseWheelEvent
                    });
                    Pump();
                    Equal(top, first.TranslatePoint(new Point(), viewport), "mouse wheel cannot move the excerpt");
                    Equal(clip, body.Clip.Bounds, "mouse wheel cannot reveal more content");
                }

                // Blank lines now have the same natural height as the note. Fifteen source
                // lines fit here and still exercise content beyond the former twelve-block cap.
                source = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"正文 {i}"));
                invalidation.Invalidate();
                Pump();
                body = viewport.Children.OfType<StackPanel>().Single();
                var finalParagraph = (FrameworkElement)body.Children[^1];
                Require(EdgePreviewText(finalParagraph).Contains("正文 8"), "blank lines do not exhaust an arbitrary visible block count");
                Require(finalParagraph.TranslatePoint(new Point(0, finalParagraph.ActualHeight), viewport).Y <= body.Clip.Bounds.Bottom,
                    "later paragraph is actually visible when the card has room");
                Equal(0.0, indicator.Opacity, "fitting content has no ellipsis");

                window.Height = 180;
                Pump();
                Equal(1.0, indicator.Opacity, "a smaller viewport recomputes overflow");
                window.Height = 410;
                Pump();
                body = viewport.Children.OfType<StackPanel>().Single();
                Equal(0.0, indicator.Opacity, "restoring space removes the overflow indicator");
                Require(EdgePreviewText(body).Contains("正文 8"), "growing the viewport restores the previously hidden tail");
                source = "";
                invalidation.Invalidate();
                Pump();
                Equal(0.0, indicator.Opacity, "empty content does not retain overflow state");
            }
            finally
            {
                window.Close();
                Pump();
            }
        });

        check("Edge note preview does not discard ordinary source before layout", () =>
        {
            var source = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"正文 {i}"));
            var panel = new StackPanel();
            var truncated = MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, source, _ => { });
            Require(EdgePreviewText(panel).Contains("正文 10"), "later paragraphs remain available for layout");
            Require(!truncated, "ordinary note is not truncated");

            source = new string('文', 700) + "\n段落之后";
            truncated = MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, source, _ => { });
            Require(EdgePreviewText(panel).Contains("段落之后"), "long paragraph does not discard following text");
            Require(!truncated, "ordinary long paragraph is not truncated before layout");
        });

        check("Edge note preview builds only a viewport-sized prefix without changing its content", () =>
        {
            var source = string.Join("\n\n", Enumerable.Range(1, 200)
                .Select(i => $"**段落 {i}** [链接](https://example.com)"));
            foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            {
                var eager = new StackPanel();
                MarkdownEdgeCapsulePreviewRenderer.RenderInto(eager, source, _ => { }, mode);
                foreach (var size in new[] { new Size(180, 120), new Size(420, 340) })
                {
                    var bounded = new StackPanel();
                    var truncated = MarkdownEdgeCapsulePreviewRenderer.RenderInto(bounded, source, _ => { }, mode, size);
                    Require(truncated, "omitted source is reported to the overflow indicator");
                    Require(bounded.Children.Count < eager.Children.Count / 2, "hidden blocks are not instantiated");
                    bounded.Measure(new Size(size.Width, double.PositiveInfinity));
                    Require(bounded.DesiredSize.Height >= size.Height, "actual wrapped content fills the viewport");
                    for (var i = 0; i < bounded.Children.Count; i++)
                    {
                        Equal(EdgePreviewText(eager.Children[i]), EdgePreviewText(bounded.Children[i]),
                            "visible prefix preserves source order and Markdown semantics");
                    }
                    Console.WriteLine($"  Edge preview {mode} {size}: {eager.Children.Count} -> {bounded.Children.Count} blocks");
                }

                var paragraph = new string('文', 700);
                var single = new StackPanel();
                MarkdownEdgeCapsulePreviewRenderer.RenderInto(single, paragraph + "\n后续内容", _ => { }, mode, new Size(180, 120));
                Equal(1, single.Children.Count, "a wrapping paragraph alone can fill the card");
                Equal(paragraph, EdgePreviewText(single.Children[0]), "viewport budgeting does not reinstate the 512-character cutoff");
            }
        });

        check("Edge note preview layout contains render failures and retries on invalidation", () =>
        {
            var viewport = new MarkdownEdgeCapsulePreviewViewport(new StackPanel());
            var attempts = 0;
            viewport.SetContent((_, _) => { attempts++; throw new InvalidOperationException("optional preview"); });
            var size = new Size(300, 180);
            viewport.Measure(size);
            viewport.Arrange(new Rect(size));
            Equal(0, attempts, "detached layout never invokes the content factory");
            var window = new Window { Content = viewport, Width = 300, Height = 180, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                viewport.InvalidateMeasure();
                Pump();
                Equal(1, attempts, "failed rendering is not repeated on every measure");
                viewport.SetContent((target, bounds) => MarkdownEdgeCapsulePreviewRenderer.RenderSteps(
                    target, "恢复内容", _ => { }, MarkdownRenderModes.Full, bounds));
                Pump();
                var body = viewport.Children.OfType<StackPanel>().Single();
                Require(EdgePreviewText(body).Contains("恢复内容"), "new content can recover after an optional failure");
                Equal(0.0, viewport.Children.OfType<TextBlock>().Single().Opacity, "recovery clears stale overflow");
                viewport.SetContent((_, _) => throw new InvalidOperationException("refresh failed"));
                Pump();
                Require(ReferenceEquals(body, viewport.Children.OfType<StackPanel>().Single()), "failed refresh preserves the published excerpt");
            }
            finally
            {
                window.Close();
                Pump();
            }
        });

        check("Edge note preview yields to input without rendering in shell layout or publishing partial text", () =>
        {
            var source = string.Join("\n", Enumerable.Repeat("**正文** [链接](https://example.com)", 200));
            var original = new StackPanel();
            var viewport = new MarkdownEdgeCapsulePreviewViewport(original);
            var stepped = 0;
            var inputRan = false;
            IEnumerable<bool> ObserveRender(Panel target, Size size)
            {
                foreach (var truncated in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(target, source, _ => { }, MarkdownRenderModes.Full, size))
                {
                    if (++stepped == 1)
                    {
                        viewport.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                        {
                            inputRan = true;
                            Require(stepped <= 4, "input runs before the next rendering batch");
                            Equal(0, original.Children.Count, "partial content does not replace the published tree");
                            Equal(0.0, target.Opacity, "staging content remains invisible");
                            Require(!target.IsHitTestVisible, "staging links cannot receive input");
                            Require(viewport.ActualWidth > 0 && viewport.ActualHeight > 0, "shell already has a usable layout");
                            var before = stepped;
                            viewport.InvalidateMeasure();
                            viewport.UpdateLayout();
                            Equal(before, stepped, "forced shell layout does not advance Markdown rendering");
                        }));
                    }
                    yield return truncated;
                }
            }
            viewport.SetContent(ObserveRender);
            viewport.Measure(new Size(400, 340));
            viewport.Arrange(new Rect(0, 0, 400, 340));
            Equal(0, stepped, "initial Measure/Arrange does no Markdown work");
            var window = new Window { Content = viewport, Width = 420, Height = 380, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                Require(inputRan && stepped > 4, "real Markdown rendering spans multiple dispatcher turns");
                var body = viewport.Children.OfType<StackPanel>().Single();
                var expected = new StackPanel();
                MarkdownEdgeCapsulePreviewRenderer.RenderInto(expected, source, _ => { }, MarkdownRenderModes.Full,
                    new Size(viewport.ActualWidth, viewport.ActualHeight));
                Equal(EdgePreviewText(expected), EdgePreviewText(body), "atomic publication matches synchronous rendering");
                Equal(1.0, body.Opacity, "completed excerpt is visible");
            }
            finally
            {
                window.Close();
                Pump();
            }
        });

        check("Edge note preview cancels stale content, retraction and unloaded builds", () =>
        {
            foreach (var boundary in new[] { "replace", "retract", "unload" })
            {
                var original = new StackPanel();
                var viewport = new MarkdownEdgeCapsulePreviewViewport(original);
                var window = new Window { Content = viewport, Width = 420, Height = 380, ShowInTaskbar = false };
                var stepped = 0;
                var disposed = false;
                IEnumerable<bool> OldRender(Panel target, Size size)
                {
                    try
                    {
                        var source = string.Join("\n", Enumerable.Repeat("旧内容", 200));
                        foreach (var truncated in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(target, source, _ => { }, MarkdownRenderModes.Full, size))
                        {
                            if (++stepped == 1)
                            {
                                viewport.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                                {
                                    if (boundary == "replace")
                                        viewport.SetContent((panel, bounds) => MarkdownEdgeCapsulePreviewRenderer.RenderSteps(panel, "新内容", _ => { }, MarkdownRenderModes.Full, bounds));
                                    else if (boundary == "retract")
                                        viewport.SetPreviewActive(false);
                                    else
                                        window.Content = null;
                                }));
                            }
                            yield return truncated;
                        }
                    }
                    finally { disposed = true; }
                }
                viewport.SetContent(OldRender);
                try
                {
                    window.Show();
                    Pump();
                    Require(stepped is > 0 and <= 4 && disposed, $"{boundary} stops and disposes the old iterator");
                    var body = viewport.Children.OfType<StackPanel>().Single();
                    Require(!EdgePreviewText(body).Contains("旧内容"), $"{boundary} never publishes a late result");
                    if (boundary == "replace")
                    {
                        Require(EdgePreviewText(body).Contains("新内容"), "replacement wins over the older asynchronous task");
                    }
                    else
                    {
                        Require(ReferenceEquals(original, body), "cancelled build keeps the prior published surface");
                        viewport.SetContent((panel, bounds) => MarkdownEdgeCapsulePreviewRenderer.RenderSteps(panel, "恢复", _ => { }, MarkdownRenderModes.Full, bounds));
                        viewport.SetPreviewActive(true);
                        window.Content = viewport;
                        Pump();
                        Require(EdgePreviewText(viewport).Contains("恢复"), "reactivation and reattachment can prepare fresh content");
                    }
                }
                finally
                {
                    window.Close();
                    Pump();
                }
            }
        });

        check("Edge note preview yields while scanning a long fenced block", () =>
        {
            var source = "```\n" + string.Join("\n", Enumerable.Repeat("code", 100)) + "\n```";
            var panel = new StackPanel();
            using var steps = MarkdownEdgeCapsulePreviewRenderer.RenderSteps(panel, source, _ => { }).GetEnumerator();
            for (var i = 0; i < 4; i++)
            {
                Require(steps.MoveNext(), "each fence source line is a separate step");
                Equal(0, panel.Children.Count, "initial steps do not scan and build the whole code fence");
            }
            while (steps.MoveNext()) { }
            Equal(1, panel.Children.Count, "completed fence remains one formatted code block");
            Require(EdgePreviewText(panel).Contains("code"), "code content survives cooperative preparation");
        });

        check("Edge note preview still bounds pathological documents", () =>
        {
            foreach (var source in new[] { new string('文', 100000), string.Concat(Enumerable.Repeat("x\n", 10000)) })
            {
                var panel = new StackPanel();
                var truncated = MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, source, _ => { });
                Require(panel.Children.Count < 200, "visual tree remains bounded");
                var text = EdgePreviewText(panel);
                Require(text.Length < 20000 && truncated, "bounded text reports source truncation to the viewport");
            }
        });

        RunEdgePreviewAppearanceChecks(check);
    }

    private static IEnumerable<DependencyObject> EdgePreviewElements(DependencyObject element)
    {
        yield return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            foreach (var child in EdgePreviewElements(VisualTreeHelper.GetChild(element, i)))
            {
                yield return child;
            }
        }
    }

    private static string EdgePreviewText(DependencyObject element)
    {
        if (element is TextBlock text)
        {
            return new TextRange(text.ContentStart, text.ContentEnd).Text;
        }

        return string.Join("\n", Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element))
            .Select(i => EdgePreviewText(VisualTreeHelper.GetChild(element, i))));
    }
}
