using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

// Export owns a portable copy only. The original paper, plugin and archive keep their ownership.
internal static class NoteHtmlExport
{
    internal static string Render(string noteId, string title, string content, NoteImageStore images)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .DisableHtml()
            .UsePreciseSourceLocation()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseTaskLists()
            .Build();
        var exportContent = PrepareMarkdownForExport(content, out var imageWidths);
        var document = Markdown.Parse(exportContent, pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage)
            {
                var url = link.Url ?? "";
                if (!url.StartsWith("i:", StringComparison.Ordinal) ||
                    !images.TryGetEncodedImageBytes(url[2..], out var asset, out var bytes) ||
                    !string.Equals(asset.NoteId, noteId, StringComparison.Ordinal))
                    throw new InvalidDataException(Strings.Get("NoteHtmlMissingImage") + " " + url);
                link.Url = "data:" + asset.Mime + ";base64," + Convert.ToBase64String(bytes);
                if (imageWidths.TryGetValue(link.Span.Start, out var displayOptions))
                    ApplyImageWidth(link, displayOptions, asset.Width);
            }
            else if (!(link.Url?.StartsWith('#') == true ||
                Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"))
                link.Url = "";
        }
        var body = document.ToHtml(pipeline);
        var safeTitle = WebUtility.HtmlEncode(title);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">" +
            "<title>" + safeTitle + "</title><style>body{max-width:900px;margin:40px auto;padding:0 24px;background:#faf8f1;color:#302e29;font:16px/1.8 system-ui,sans-serif}img{max-width:100%;height:auto}pre{overflow:auto;padding:16px;background:#eeeae0}blockquote{border-left:3px solid #aaa;padding-left:18px}a{color:#52745d}h1,h2,h3{line-height:1.4}header{border-bottom:1px solid #ddd;margin-bottom:28px}header small{color:#777}@media print{body{background:white}}</style></head><body><header><h1>" +
            safeTitle + "</h1><small>" + DateTime.Now.ToString("yyyy-MM-dd") + " · PaperTodo</small></header>" + body + "</body></html>";
    }

    // Normalize PaperTodo's standalone image lines before Markdig so {width=...}
    // does not appear as visible text in the exported HTML.
    private static string PrepareMarkdownForExport(
        string markdown, out Dictionary<int, MarkdownImageDisplayOptions> widths)
    {
        widths = new Dictionary<int, MarkdownImageDisplayOptions>();
        var references = MarkdownImageReferences.Enumerate(markdown).ToArray();
        if (references.Length == 0) return markdown;

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (var reference in references)
        {
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

    internal static void Save(string path, string html)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(directory,
            "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(html);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
