using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static T AwaitPreload<T>(Task<T> task)
    {
        var until = Stopwatch.StartNew();
        while (!task.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(12)) Pump();
        Require(task.IsCompleted, "optional preload completes/cancels rather than hanging");
        return task.GetAwaiter().GetResult();
    }

    private static void PreloadChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.DarkRed;
        root.Resources["WeakTextBrushKey"] = Brushes.Gray;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        root.Resources["HoverBrushKey"] = Brushes.LightGray;
        root.Resources["PaperBorderBrushKey"] = Brushes.Gray;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        var size = new EdgeCapsulePreviewSize(460, 410);
        var text = "# Heading **bold**\n> quote [q](https://example.com/q)\n- [x] done `code`\n12) ordered *italic*\n---\n![image](i:123456)\n```\n\nliteral **code**\n```\n" + new string('文', 450);
        var mode = MarkdownRenderModes.Full;
        EdgeCapsulePreviewContext Context() => new(new PaperData(), () => "Preview", false, () => text, () => mode,
            (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
        bool Warm(EdgeCapsulePreviewContext context) => AwaitPreload(cache.WarmLayoutAsync(new(context, root, size, () => true, cache.Capture(context))));
        Border Demand(EdgeCapsulePreviewContext context)
        {
            cache.BeginDemand();
            var view = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context).CreateContent(size);
            var border = new Border { Width = size.ContentSize.Width, Height = size.ContentSize.Height, Child = view };
            root.Children.Add(border);
            ((EdgeCapsuleLivePreviewView)view).PrepareForFirstDisplay();
            border.Measure(new Size(border.Width, border.Height));
            border.Arrange(new Rect(0, 0, border.Width, border.Height));
            UntilReview(() => Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single().Opacity == 1, "preview publishes complete content");
            return border;
        }
        void Release(Border border) { root.Children.Remove(border); border.Child = null; Pump(); }
        byte[] Pixels(FrameworkElement element)
        {
            element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(element);
            var width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var bytes = new byte[width * height * 4]; bitmap.CopyPixels(bytes, width * 4, 0); return bytes;
        }
        try
        {
            window.Show(); Pump();
            foreach (var (renderMode, sharp, zoom) in new[]
            {
                (MarkdownRenderModes.Off, false, 1.0), (MarkdownRenderModes.Off, true, 1.3),
                (MarkdownRenderModes.Basic, false, 0.7), (MarkdownRenderModes.Basic, true, 1.3),
                (MarkdownRenderModes.Full, false, 0.7), (MarkdownRenderModes.Full, true, 1.3)
            })
            {
                mode = renderMode;
                AppTypography.Configure(sharp ? UiFontPresets.YaHei : UiFontPresets.Default,
                    sharp ? 1.25 : 1, textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                NoteTypography.Configure(sharp ? VisualTextSizes.Large : VisualTextSizes.Medium, sharp);
                cache.Clear();
                var context = Context(); context.Paper.TextZoom = zoom;
                Require(Warm(context) && root.Children.Count == 0, "preparation completes without mounting hidden controls");
                var hot = Demand(context); var hotPixels = Pixels(hot); Release(hot);
                cache.Clear();
                var cold = Demand(context); var coldPixels = Pixels(cold); Release(cold);
                Require(hotPixels.Length == coldPixels.Length && hotPixels.Zip(coldPixels).All(pair => Math.Abs(pair.First - pair.Second) <= 32),
                    $"preparation preserves visible pixels: {renderMode}/{sharp}/{zoom}");
            }
            mode = MarkdownRenderModes.Full;
            cache.Clear();
            text = string.Concat(Enumerable.Repeat("**before** *content* `code` ", 80));
            var changedContext = Context(); Require(Warm(changedContext), "prepare old content");
            text = string.Concat(Enumerable.Repeat("**updated** *content* `code` ", 80));
            changedContext.InvalidationSource.Invalidate();
            var changed = Demand(changedContext);
            Require(PreviewText(changed).Contains("updated") && !PreviewText(changed).Contains("before"), "an edit cannot mount a stale prepared body");
            Release(changed);
            cache.Clear();
            using var cancellation = new CancellationTokenSource();
            var cancelled = cache.WarmLayoutAsync(new(changedContext, root, size, () => true, cache.Capture(changedContext)), cancellation.Token);
            cancellation.Cancel();
            try { AwaitPreload(cancelled); } catch (OperationCanceledException) { }
            Pump();
            Require(cache.ArtifactCount == 0 && root.Children.Count == 0, "cancelled preparation leaves no published result or hidden controls");
            var late = cache.WarmLayoutAsync(new(changedContext, root, size, () => true, cache.Capture(changedContext)));
            changedContext.InvalidationSource.Invalidate();
            Require(!AwaitPreload(late) && cache.ArtifactCount == 0, "late preparation cannot publish an obsolete generation");
            Console.WriteLine("PASS six cold/warm presentations, current content and cancelled/stale preparation");
        }
        finally
        {
            cache.Clear(); window.Close(); Pump();
            AppTypography.Configure(UiFontPresets.Default); NoteTypography.Configure(VisualTextSizes.Medium, false);
        }
    }
}
