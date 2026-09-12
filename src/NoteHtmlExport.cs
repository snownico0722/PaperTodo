using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

// Export owns a portable copy only. The original paper, plugin and archive keep their ownership.
internal static class NoteHtmlExport
{
    internal static string Render(string title, string content, NoteImageStore images)
    {
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
        var document = Markdown.Parse(MarkdownImageReferences.StripRenderMarkers(content), pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage)
            {
                var url = link.Url ?? "";
                if (!url.StartsWith("i:", StringComparison.Ordinal) ||
                    !images.TryGetEncodedImageBytes(url[2..], out var asset, out var bytes))
                    throw new InvalidDataException(Strings.Get("NoteHtmlMissingImage") + " " + url);
                link.Url = "data:" + asset.Mime + ";base64," + Convert.ToBase64String(bytes);
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

    internal static void Save(string path, string html)
    {
        DurableAtomicFileWriter.Shared.Write(path, new UTF8Encoding(false).GetBytes(html),
            candidate => File.ReadAllText(candidate, Encoding.UTF8) == html);
    }
}
