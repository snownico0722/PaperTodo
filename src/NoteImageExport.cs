using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

// An immutable export artifact, never another editor or a screenshot of the live surface.
// Fixed typography makes the saved image independent of window width, zoom and theme.
internal sealed record NoteImageArtifact(DrawingGroup Drawing, double Height, int MissingImages)
{
    internal const double Width = 600;
    internal const double Scale = 2;
    internal int PixelWidth => (int)(Width * Scale);
    internal int PixelHeight => checked((int)Math.Ceiling(Height * Scale));
}

internal sealed record NoteImageSource(BitmapSource Bitmap, int NaturalWidth);

internal enum NoteImageFormat { Jpeg, Png }

internal static class NoteImageExport
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UsePreciseSourceLocation()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UseTaskLists();
        // Keep HTML as inline tokens, then apply only the editor's validated
        // formatting spans. Block/unsupported HTML remains visible source text.
        var htmlBlocks = builder.BlockParsers.Find<HtmlBlockParser>();
        if (htmlBlocks != null) builder.BlockParsers.Remove(htmlBlocks);
        return builder.Build();
    }

    internal static Task<T> OnStaAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (OperationCanceledException) { completion.SetCanceled(); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "PaperTodo image export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static NoteImageArtifact Compose(string title, string markdown,
        Func<string, NoteImageSource?> resolveImage, string missingLabel, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var source = NoteHtmlExport.PrepareMarkdownForExport(markdown, out var imageWidths);
        var document = Markdown.Parse(source, Pipeline);
        var html = source.Contains('<') ? MarkdownSemanticSnapshot.Parse(source) : null;
        return new Composer(resolveImage, missingLabel, imageWidths, html, cancellation).Compose(title, document);
    }

    internal static NoteImageSource? ReadImage(NoteImageStore store, string noteId, string imageId)
    {
        if (!store.TryGetEncodedImageBytes(imageId, out var asset, out var bytes) ||
            !string.Equals(asset.NoteId, noteId, StringComparison.Ordinal)) return null;
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Min(1200, Math.Max(1, asset.Width));
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return new NoteImageSource(bitmap, asset.Width);
    }

    internal static void Save(string path, NoteImageArtifact artifact, CancellationToken cancellation,
        NoteImageFormat format = NoteImageFormat.Jpeg)
    {
        if (format == NoteImageFormat.Png)
            NotePortableExport.SaveAtomic(path, stream => Write(stream, artifact, cancellation));
        else
            NotePortableExport.SaveAtomicFile(path, tempPath => WriteJpeg(tempPath, artifact, cancellation));
    }

    internal static void Write(Stream stream, NoteImageArtifact artifact, CancellationToken cancellation)
    {
        // Preserve the lossless PNG path.
        using var png = new NoteImagePngWriter(stream, artifact.PixelWidth, artifact.PixelHeight);
        RenderBands(artifact, cancellation, png.WriteBgrRows);
        cancellation.ThrowIfCancellationRequested();
        png.Complete();
    }

    internal static void WriteJpeg(string path, NoteImageArtifact artifact, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var jpeg = new NoteImageJpegWriter(path, artifact.PixelWidth, artifact.PixelHeight);
        RenderBands(artifact, cancellation, jpeg.WriteBgrRows);
        cancellation.ThrowIfCancellationRequested();
        jpeg.Complete();
    }

    private static void RenderBands(NoteImageArtifact artifact, CancellationToken cancellation,
        Action<byte[], int> writeRows)
    {
        // Both formats use bounded strips, never a whole-document bitmap.
        const int bandHeight = 512;
        var stride = checked(artifact.PixelWidth * 3);
        var pixels = new byte[stride * bandHeight];
        for (var top = 0; top < artifact.PixelHeight; top += bandHeight)
        {
            cancellation.ThrowIfCancellationRequested();
            var height = Math.Min(bandHeight, artifact.PixelHeight - top);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new TranslateTransform(0, -top));
                drawing.PushTransform(new ScaleTransform(NoteImageArtifact.Scale, NoteImageArtifact.Scale));
                drawing.DrawDrawing(artifact.Drawing);
            }
            var bitmap = new RenderTargetBitmap(artifact.PixelWidth, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            // WPF's antialiasing can leave alpha=254 even over an opaque background.
            // Convert premultiplied pixels through WIC before writing opaque RGB;
            // simply discarding alpha would slightly darken those edge pixels.
            var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
            opaque.CopyPixels(pixels, stride, 0);
            writeRows(pixels, height);
        }
    }

    [Flags]
    private enum Style { Normal = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8, Link = 16, Underline = 32 }
    private sealed record RunStyle(int Start, int Length, Style Style);

    private sealed class Composer(Func<string, NoteImageSource?> resolveImage, string missingLabel,
        IReadOnlyDictionary<int, MarkdownImageDisplayOptions> widths,
        MarkdownSemanticSnapshot? html, CancellationToken token)
    {
        private static readonly Brush Paper = Brush("#FCFAF5");
        private static readonly Brush Ink = Brush("#302D29");
        private static readonly Brush Muted = Brush("#80796F");
        private static readonly Brush Accent = Brush("#99634C");
        private static readonly Brush Rule = Brush("#DDD6CA");
        private static readonly Brush CodeFill = Brush("#F0ECE4");
        private static readonly Brush QuoteFill = Brush("#F4F0E8");
        private static readonly FontFamily BodyFont = new("Microsoft YaHei, Yu Gothic UI, Malgun Gothic, Segoe UI");
        private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Microsoft YaHei");
        private const double Margin = 48;
        private const double BodySize = 18;
        private readonly DrawingGroup _content = new();
        private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
        private DrawingContext _dc = null!;
        private double _y = 48;

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        internal NoteImageArtifact Compose(string title, MarkdownDocument document)
        {
            using (_dc = _content.Open())
            {
                _dc.DrawRoundedRectangle(Accent, null, new Rect(Margin, _y, 30, 3), 1.5, 1.5);
                _y += 25;
                // The document's opening H1 is the article title. A short paper label
                // remains the suggested filename, not a second competing title.
                var headingOwnsTitle = document.FirstOrDefault() is HeadingBlock { Level: 1 };
                if (!headingOwnsTitle && !string.IsNullOrWhiteSpace(title))
                {
                    Text(title, [], Margin, NoteImageArtifact.Width - 2 * Margin, 30, true);
                    _y += 24;
                }
                Blocks(document, Margin, NoteImageArtifact.Width - 2 * Margin);
                _y += 28;
                _dc.DrawLine(new Pen(Rule, 1), new Point(Margin, _y),
                    new Point(NoteImageArtifact.Width - Margin, _y));
                _y += 24;
                Footer();
                _y += 48;
            }
            var result = new DrawingGroup();
            using (var dc = result.Open())
            {
                dc.DrawRectangle(Paper, null, new Rect(0, 0, NoteImageArtifact.Width, Math.Ceiling(_y)));
                dc.DrawDrawing(_content);
            }
            result.Freeze();
            return new NoteImageArtifact(result, Math.Ceiling(_y), _missing.Count);
        }

        private void Blocks(ContainerBlock blocks, double x, double width)
        {
            foreach (var block in blocks)
            {
                token.ThrowIfCancellationRequested();
                switch (block)
                {
                    case HeadingBlock heading:
                        _y += heading.Level == 1 ? 4 : 14;
                        Inlines(heading.Inline, x, width, heading.Level switch
                        { 1 => 30, 2 => 24, 3 => 21, _ => 18 }, bold: true);
                        _y += 12;
                        break;
                    case ParagraphBlock paragraph:
                        Inlines(paragraph.Inline, x, width);
                        _y += 14;
                        break;
                    case QuoteBlock quote:
                        var quoteTop = _y;
                        var quoteDrawing = new DrawingGroup();
                        var outer = _dc;
                        using (_dc = quoteDrawing.Open())
                        {
                            _y += 14;
                            Blocks(quote, x + 20, Math.Max(36, width - 36));
                            _y += 2;
                        }
                        _dc = outer;
                        _dc.DrawRoundedRectangle(QuoteFill, null, new Rect(x, quoteTop, width, _y - quoteTop), 5, 5);
                        _dc.DrawRectangle(Accent, null, new Rect(x, quoteTop + 12, 2, Math.Max(8, _y - quoteTop - 24)));
                        _dc.DrawDrawing(quoteDrawing);
                        _y += 18;
                        break;
                    case ListBlock list:
                        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                        foreach (var item in list.OfType<ListItemBlock>())
                        {
                            var task = item.OfType<ParagraphBlock>().FirstOrDefault()?.Inline?.FirstChild as TaskList;
                            if (task != null) Checkbox(x + 2, _y + 7, task.Checked);
                            else TextAt(list.IsOrdered ? number + "." : "•", x, _y, 30, BodySize, Muted);
                            var before = _y;
                            Blocks(item, x + 30, Math.Max(36, width - 30));
                            _y = Math.Max(_y - (list.IsLoose ? 0 : 8), before + 32);
                            number++;
                        }
                        _y += 10;
                        break;
                    case CodeBlock code:
                        var value = code.Lines.ToString();
                        var formatted = Format(value.Length == 0 ? " " : value, width - 28, 15, false, CodeFont);
                        formatted.LineHeight = 25;
                        _dc.DrawRoundedRectangle(CodeFill, null, new Rect(x, _y, width, formatted.Height + 28), 7, 7);
                        _dc.DrawText(formatted, new Point(x + 14, _y + 14));
                        _y += formatted.Height + 46;
                        break;
                    case ThematicBreakBlock:
                        _y += 10;
                        _dc.DrawLine(new Pen(Rule, 1), new Point(x, _y), new Point(x + width, _y));
                        _y += 25;
                        break;
                    case ContainerBlock container:
                        Blocks(container, x, width);
                        break;
                    case LeafBlock leaf:
                        Inlines(leaf.Inline, x, width);
                        _y += 14;
                        break;
                }
            }
        }

        private void Inlines(ContainerInline? container, double x, double width,
            double size = BodySize, bool bold = false)
        {
            var text = new StringBuilder();
            var styles = new List<RunStyle>();
            void Flush()
            {
                if (text.Length == 0) return;
                Text(text.ToString(), styles, x, width, size, bold);
                text.Clear(); styles.Clear();
            }
            void Append(string value, Style style)
            {
                styles.Add(new RunStyle(text.Length, value.Length, style));
                text.Append(value);
            }
            void Visit(ContainerInline? parent, Style style)
            {
                for (var inline = parent?.FirstChild; inline != null; inline = inline.NextSibling)
                {
                    token.ThrowIfCancellationRequested();
                    var inlineStyle = style | HtmlStyle(inline);
                    switch (inline)
                    {
                        case LiteralInline literal: Append(literal.Content.ToString(), inlineStyle); break;
                        case HtmlEntityInline entity: Append(entity.Transcoded.ToString(), inlineStyle); break;
                        case HtmlInline tag:
                            if (!IsHtmlMarker(tag)) Append(tag.Tag, inlineStyle);
                            break;
                        case CodeInline code: Append(code.Content, inlineStyle | Style.Code); break;
                        case LineBreakInline: Append("\n", inlineStyle); break;
                        case AutolinkInline link: Append(link.Url, inlineStyle | Style.Link); break;
                        case TaskList: break; // The list row draws its own vector checkbox.
                        case EmphasisInline emphasis:
                            Visit(emphasis, inlineStyle | (emphasis.DelimiterChar == '~' ? Style.Strike :
                                emphasis.DelimiterCount >= 2 ? Style.Bold : Style.Italic));
                            break;
                        case LinkInline { IsImage: true } image:
                            Flush();
                            Image(image, x, width);
                            break;
                        case LinkInline link: Visit(link, inlineStyle | Style.Link); break;
                        case ContainerInline nested: Visit(nested, inlineStyle); break;
                    }
                }
            }
            Visit(container, Style.Normal);
            Flush();
        }

        private bool IsHtmlMarker(HtmlInline tag)
        {
            if (html == null) return false;
            foreach (var span in html.SpansForLine(tag.Line))
                if (span.Kind == MarkdownSemanticSpanKind.HtmlMarker &&
                    span.Start == tag.Span.Start && span.End == tag.Span.End + 1) return true;
            return false;
        }

        private Style HtmlStyle(Inline inline)
        {
            if (html == null) return Style.Normal;
            var style = Style.Normal;
            foreach (var span in html.SpansForLine(inline.Line))
            {
                if (inline.Span.Start < span.Start || inline.Span.Start >= span.End) continue;
                style |= span.Kind switch
                {
                    MarkdownSemanticSpanKind.HtmlStrong => Style.Bold,
                    MarkdownSemanticSpanKind.HtmlEmphasis => Style.Italic,
                    MarkdownSemanticSpanKind.HtmlStrikethrough => Style.Strike,
                    MarkdownSemanticSpanKind.HtmlUnderline => Style.Underline,
                    MarkdownSemanticSpanKind.HtmlCode => Style.Code,
                    _ => Style.Normal
                };
            }
            foreach (var link in html.LinksForLine(inline.Line))
                if (inline.Span.Start >= link.LabelStart && inline.Span.Start < link.LabelEnd)
                    style |= Style.Link;
            return style;
        }

        private static FormattedText Format(string text, double width, double size,
            bool bold = false, FontFamily? font = null, Brush? color = null) => new(
                text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font ?? BodyFont, FontStyles.Normal,
                    bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, color ?? Ink, 1)
            {
                MaxTextWidth = Math.Max(36, width),
                LineHeight = size * (size >= 24 ? 1.5 : 1.8),
                Trimming = TextTrimming.None
            };

        private void Text(string value, IReadOnlyList<RunStyle> styles, double x, double width,
            double size, bool bold)
        {
            var formatted = Format(value, width, size, bold);
            foreach (var run in styles.Where(run => run.Length > 0))
            {
                if (run.Style.HasFlag(Style.Bold)) formatted.SetFontWeight(FontWeights.Bold, run.Start, run.Length);
                if (run.Style.HasFlag(Style.Italic)) formatted.SetFontStyle(FontStyles.Italic, run.Start, run.Length);
                var decorations = new TextDecorationCollection();
                if (run.Style.HasFlag(Style.Strike))
                    foreach (var decoration in TextDecorations.Strikethrough) decorations.Add(decoration);
                if (run.Style.HasFlag(Style.Underline) || run.Style.HasFlag(Style.Link))
                    foreach (var decoration in TextDecorations.Underline) decorations.Add(decoration);
                if (decorations.Count > 0) formatted.SetTextDecorations(decorations, run.Start, run.Length);
                if (run.Style.HasFlag(Style.Link))
                {
                    formatted.SetForegroundBrush(Accent, run.Start, run.Length);
                }
                if (run.Style.HasFlag(Style.Code))
                {
                    formatted.SetFontFamily(CodeFont, run.Start, run.Length);
                    formatted.SetFontSize(size * .9, run.Start, run.Length);
                }
            }
            foreach (var run in styles.Where(run => run.Length > 0 && run.Style.HasFlag(Style.Code)))
                _dc.DrawGeometry(CodeFill, null, formatted.BuildHighlightGeometry(new Point(x, _y), run.Start, run.Length));
            _dc.DrawText(formatted, new Point(x, _y));
            _y += formatted.Height;
        }

        private void TextAt(string value, double x, double y, double width, double size, Brush brush) =>
            _dc.DrawText(Format(value, width, size, color: brush), new Point(x, y));

        private void Image(LinkInline link, double x, double width)
        {
            var url = link.Url ?? "";
            var id = url.StartsWith("i:", StringComparison.Ordinal) ? url[2..] : url;
            NoteImageSource? image = null;
            if (url.StartsWith("i:", StringComparison.Ordinal)) image = resolveImage(id);
            if (image == null)
            {
                _missing.Add(id);
                Text("[" + missingLabel + "]", [], x, width, BodySize, false);
                _y += 12;
                return;
            }
            widths.TryGetValue(link.Span.Start, out var options);
            var bitmap = image.Bitmap;
            var imageWidth = Math.Min(width, options.ResolveWidth(image.NaturalWidth, width));
            var imageHeight = imageWidth * bitmap.PixelHeight / bitmap.PixelWidth;
            _y += 8;
            var bounds = new Rect(x + (width - imageWidth) / 2, _y, imageWidth, imageHeight);
            _dc.DrawRectangle(Brushes.White, null, bounds);
            _dc.DrawImage(bitmap, bounds);
            _y += imageHeight + 12;
        }

        private void Checkbox(double x, double y, bool check)
        {
            _dc.DrawRoundedRectangle(check ? Accent : null, new Pen(Accent, 1.3), new Rect(x, y, 14, 14), 3, 3);
            if (!check) return;
            var pen = new Pen(Brushes.White, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _dc.DrawLine(pen, new Point(x + 3, y + 7), new Point(x + 6, y + 10));
            _dc.DrawLine(pen, new Point(x + 6, y + 10), new Point(x + 11, y + 4));
        }

        private void Footer()
        {
            var wordmark = Format("PaperToDo", 140, 13, color: Muted);
            var left = (NoteImageArtifact.Width - wordmark.Width - 29) / 2;
            var pen = new Pen(Muted, 1.1) { LineJoin = PenLineJoin.Round };
            var icon = new StreamGeometry();
            using (var g = icon.Open())
            {
                g.BeginFigure(new Point(left, _y + 2), false, true);
                g.LineTo(new Point(left + 10, _y + 2), true, false);
                g.LineTo(new Point(left + 16, _y + 8), true, false);
                g.LineTo(new Point(left + 16, _y + 20), true, false);
                g.LineTo(new Point(left, _y + 20), true, false);
            }
            _dc.DrawGeometry(null, pen, icon);
            _dc.DrawLine(pen, new Point(left + 10, _y + 2), new Point(left + 10, _y + 8));
            _dc.DrawLine(pen, new Point(left + 10, _y + 8), new Point(left + 16, _y + 8));
            _dc.DrawLine(pen, new Point(left + 4, _y + 12), new Point(left + 12, _y + 12));
            _dc.DrawLine(pen, new Point(left + 4, _y + 16), new Point(left + 10, _y + 16));
            _dc.DrawText(wordmark, new Point(left + 29, _y));
        }

    }
}
