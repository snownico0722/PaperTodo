using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperTodo;

/// <summary>A file document. It has no PaperData, workspace or image database owner.</summary>
internal sealed class StandaloneStudioDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex HtmlCharset = new(
        "<meta\\b[^>]*?charset\\s*=\\s*[\"']?\\s*([A-Za-z0-9._-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private StandaloneStudioDocument(string? path, string text, string savedText,
        int encodingCodePage, bool hasBom, string newLine)
    {
        FilePath = path;
        Text = text;
        SavedText = savedText;
        EncodingCodePage = encodingCodePage;
        HasBom = hasBom;
        NewLine = newLine;
    }

    internal string? FilePath { get; private set; }
    internal string Text { get; set; }
    internal string SavedText { get; private set; }
    internal int EncodingCodePage { get; }
    internal bool HasBom { get; }
    internal string NewLine { get; }
    internal bool IsDirty => !string.Equals(Text, SavedText, StringComparison.Ordinal);
    internal bool IsMarkdown => FilePath == null ||
        string.Equals(Path.GetExtension(FilePath), ".md", StringComparison.OrdinalIgnoreCase);

    internal static StandaloneStudioDocument New() =>
        new(null, "", "", Encoding.UTF8.CodePage, false, Environment.NewLine);

    internal static bool IsSupportedPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt" or ".html" or ".htm";

    internal static StandaloneStudioDocument Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsSupportedPath(fullPath))
            throw new NotSupportedException("Supported document types: .md, .txt, .html, .htm.");

        var bytes = File.ReadAllBytes(fullPath);
        var (encoding, preambleLength) = DetectEncoding(bytes, fullPath);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new(fullPath, text, text, encoding.CodePage, preambleLength != 0, FindNewLine(text));
    }

    internal void Save(string path, string? text = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsSupportedPath(fullPath))
            throw new NotSupportedException("Supported document types: .md, .txt, .html, .htm.");

        var candidate = text ?? Text;
        var encoding = GetEncoding(EncodingCodePage, HasBom);
        var body = encoding.GetBytes(candidate);
        var preamble = HasBom ? encoding.GetPreamble() : [];
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        AtomicWriteBytes(fullPath, bytes);

        // Path, clean baseline and rewritten image references change together only
        // after the original file has been replaced successfully.
        FilePath = fullPath;
        Text = candidate;
        SavedText = candidate;
    }

    internal static void AtomicWriteBytes(string path, byte[] bytes) =>
        NotePortableExport.SaveAtomic(path, bytes);

    internal static StandaloneStudioDocument Restore(string? path, string text, string savedText,
        int encodingCodePage, bool hasBom, string newLine)
    {
        if (path != null && !IsSupportedPath(path))
            throw new InvalidDataException("Unsupported recovered document type.");
        if (newLine is not ("\r\n" or "\n" or "\r"))
            throw new InvalidDataException("Invalid recovered line ending.");
        _ = GetEncoding(encodingCodePage, hasBom);
        return new(path, text, savedText, encodingCodePage, hasBom, newLine);
    }

    private static Encoding GetEncoding(int codePage, bool bom)
    {
        return codePage switch
        {
            65001 => new UTF8Encoding(bom, true),
            1200 => new UnicodeEncoding(false, bom, true),
            1201 => new UnicodeEncoding(true, bom, true),
            12000 => new UTF32Encoding(false, bom, true),
            12001 => new UTF32Encoding(true, bom, true),
            _ => LegacyEncoding(codePage)
        };
    }

    private static Encoding LegacyEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes, string path)
    {
        // UTF-32 LE begins with the UTF-16 LE marker, so longer BOMs go first.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return (GetEncoding(12000, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return (GetEncoding(12001, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (GetEncoding(65001, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (GetEncoding(1200, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (GetEncoding(1201, true), 2);

        // An explicit HTML charset is stronger evidence than guessing from bytes.
        if (Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm")
        {
            var declaration = HtmlCharset.Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8192)));
            if (declaration.Success)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var declared = Encoding.GetEncoding(declaration.Groups[1].Value,
                    EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return (declared, 0);
            }
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return (StrictUtf8, 0);
        }
        catch (DecoderFallbackException)
        {
            // With the code-pages provider, 0 is Windows' active ANSI code page.
            // Strict fallback prevents silent replacement of unrepresentable text.
            return (LegacyEncoding(0), 0);
        }
    }

    private static string FindNewLine(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0) return Environment.NewLine;
        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }
}
