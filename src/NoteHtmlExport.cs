using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

// Based on GitKUMA's HTML exporter in PR #195. HTML and embedded images are
// written incrementally, without retaining a base64 copy of the entire note.
internal static class NoteHtmlExport
{
    // Keep the string entry point for focused markup tests; production exports use Write.
    internal static string Render(string noteId, string title, string content,
        NoteImageStore images, string missingImageLabel, ISet<string> missingImages)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, noteId, title, content, images, missingImageLabel, missingImages);
        return writer.ToString();
    }

    internal static void Write(TextWriter writer, string noteId, string title, string content,
        NoteImageStore images, string missingImageLabel, ISet<string> missingImages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pipeline = new MarkdownPipelineBuilder()
            .DisableHtml()
            .UsePreciseSourceLocation()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseTaskLists()
            .Build();
        var exportContent = PrepareMarkdownForExport(content, out var imageWidths, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var document = Markdown.Parse(exportContent, pipeline);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var link in document.Descendants<LinkInline>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!link.IsImage &&
                !(link.Url?.StartsWith('#') == true ||
                  Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                  uri.Scheme is "https" or "http" or "mailto"))
                link.Url = "";
        }

        var safeTitle = WebUtility.HtmlEncode(title);
        writer.Write("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">" +
            "<title>" + safeTitle + "</title><style>body{max-width:900px;margin:40px auto;padding:0 24px;background:#faf8f1;color:#302e29;font:16px/1.8 system-ui,sans-serif}img{max-width:100%;height:auto}pre{overflow:auto;padding:16px;background:#eeeae0}blockquote{border-left:3px solid #aaa;padding-left:18px}a{color:#52745d}h1,h2,h3{line-height:1.4}header{border-bottom:1px solid #ddd;margin-bottom:28px}header small{color:#777}@media print{body{background:white}}</style></head><body><header><h1>" +
            safeTitle + "</h1><small>" + DateTime.Now.ToString("yyyy-MM-dd") + " · PaperTodo</small></header>");

        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<LinkInlineRenderer>(
            new StreamingImageRenderer(noteId, images, missingImageLabel, missingImages, imageWidths, cancellationToken));
        renderer.Render(document);
        writer.Write("</body></html>");
    }

    private sealed class StreamingImageRenderer(
        string noteId, NoteImageStore images, string missingLabel,
        ISet<string> missing, IReadOnlyDictionary<int, MarkdownImageDisplayOptions> widths,
        CancellationToken cancellationToken)
        : LinkInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!link.IsImage)
            {
                base.Write(renderer, link);
                return;
            }

            var url = link.Url ?? "";
            var imageId = url.StartsWith("i:", StringComparison.Ordinal) ? url[2..] : url;
            if (!url.StartsWith("i:", StringComparison.Ordinal) ||
                !images.TryGetEncodedImageBytes(imageId, out var asset, out var bytes) ||
                !string.Equals(asset.NoteId, noteId, StringComparison.Ordinal))
            {
                missing.Add(imageId);
                renderer.WriteEscape("[" + missingLabel + ": " + imageId + "]");
                return;
            }

            if (widths.TryGetValue(link.Span.Start, out var options))
                ApplyImageWidth(link, options, asset.Width);

            // Preserve Markdig's image alt/title/attribute rendering while writing the
            // Base64 payload directly to the destination writer in bounded chunks.
            if (!renderer.EnableHtmlForInline)
            {
                renderer.WriteChildren(link);
                return;
            }

            renderer.Write("<img src=\"data:");
            renderer.Write(asset.Mime);
            renderer.Write(";base64,");
            WriteBase64(renderer.Writer, bytes, cancellationToken);
            renderer.Write('"');
            renderer.WriteAttributes(link);
            renderer.Write(" alt=\"");
            renderer.EnableHtmlForInline = false;
            try { renderer.WriteChildren(link); }
            finally { renderer.EnableHtmlForInline = true; }
            renderer.Write('"');
            if (!string.IsNullOrEmpty(link.Title))
            {
                renderer.Write(" title=\"");
                renderer.WriteEscape(link.Title);
                renderer.Write('"');
            }
            renderer.Write(" />");
        }

        private static void WriteBase64(TextWriter writer, ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
        {
            const int inputChunk = 24 * 1024; // Multiple of 3: no padding between chunks.
            var chars = ArrayPool<char>.Shared.Rent(inputChunk / 3 * 4);
            try
            {
                for (var offset = 0; offset < bytes.Length; offset += inputChunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = Math.Min(inputChunk, bytes.Length - offset);
                    if (!Convert.TryToBase64Chars(bytes.Slice(offset, length), chars, out var written))
                        throw new InvalidDataException("Could not encode an exported image.");
                    writer.Write(chars, 0, written);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }
    }

    // PaperTodo's standalone image lines carry size syntax that standard Markdown
    // would otherwise render as trailing text.
    private static string PrepareMarkdownForExport(
        string markdown, out Dictionary<int, MarkdownImageDisplayOptions> widths, CancellationToken cancellationToken)
    {
        widths = new Dictionary<int, MarkdownImageDisplayOptions>();

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var reference in MarkdownImageReferences.Enumerate(markdown))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            var line = markdown.AsSpan(reference.LineStart, reference.LineLength);
            var indentation = 0;
            while (indentation < line.Length && char.IsWhiteSpace(line[indentation]))
                indentation++;
            builder.Append(line[..indentation]);
            widths.Add(builder.Length, reference.DisplayOptions);
            builder.Append("![").Append(reference.Label).Append("](i:")
                .Append(reference.ImageId).Append(')');
            cursor = reference.LineStart + reference.LineLength;
        }
        if (cursor == 0) return markdown;
        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private static void ApplyImageWidth(
        LinkInline link, MarkdownImageDisplayOptions options, int naturalWidth)
    {
        double? width = null;
        var unit = "px";
        if (options.WidthAttribute is { } attribute)
        {
            width = attribute.IsPercent ? Math.Clamp(attribute.Value, 1, 1000) : attribute.Value;
            unit = attribute.IsPercent ? "%" : "px";
        }
        else if (options.LabelWidth is { } labelWidth)
        {
            width = labelWidth * Math.Clamp(options.LabelScalePercent ?? 100, 1, 1000) / 100.0;
        }
        else if (options.LabelScalePercent is { } percent)
        {
            width = naturalWidth * Math.Clamp(percent, 1, 1000) / 100.0;
        }

        if (width.HasValue)
            link.GetAttributes().AddProperty("style",
                "width:" + Math.Round(width.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) +
                unit + ";max-width:100%;height:auto");
    }


    internal static void Save(string path, string html) =>
        NotePortableExport.SaveAtomic(path, Encoding.UTF8.GetBytes(html));
}
