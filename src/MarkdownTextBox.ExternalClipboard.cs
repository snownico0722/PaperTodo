using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    // HTML remains the primary mixed text+image representation and intentionally has a higher
    // ceiling than RTF. Count source bytes per rendered occurrence because repeated references
    // duplicate their base64 payload in the final CF_HTML string.
    private const long MaxExternalClipboardHtmlSourceBytes = 24L * 1024 * 1024;
    // RTF stores image bytes as hexadecimal text. Keep a lower safety ceiling so an unusually
    // large selection cannot expand into an enormous UI-thread string. HTML and plain text still
    // remain available when this guard is exceeded.
    private const long MaxExternalClipboardRtfSourceBytes = 16L * 1024 * 1024;

    private readonly record struct ClipboardLine(int Start, string Text, bool HasLineBreak);

    private static void AddImageAwareExternalClipboardFormats(
        DataObject data,
        string markdown,
        IReadOnlyList<MarkdownImageReference> references,
        IReadOnlyDictionary<string, ClipboardImagePayload> images)
    {
        try
        {
            if (TryBuildExternalClipboardHtml(markdown, references, images, out var html))
            {
                data.SetData(DataFormats.Html, html, autoConvert: false);
            }
        }
        catch
        {
            // External interoperability is best-effort. The canonical Markdown fallback is added
            // by the caller after all rich formats have been attempted.
        }

        try
        {
            if (TryBuildExternalClipboardRtf(markdown, references, images, out var rtf))
            {
                data.SetData(DataFormats.Rtf, rtf, autoConvert: false);
            }
        }
        catch
        {
        }

        try
        {
            AddSingleImageClipboardFormat(data, markdown, references, images);
        }
        catch
        {
        }
    }

    private static bool TryBuildExternalClipboardHtml(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references,
        IReadOnlyDictionary<string, ClipboardImagePayload> images,
        out string html)
    {
        html = "";
        var referencesByLineStart = ClipboardReferencesByLineStart(references);
        var repeatedImageIds = references
            .GroupBy(reference => reference.ImageId, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var repeatedBase64 = new Dictionary<string, string>(StringComparer.Ordinal);
        var fragment = new StringBuilder(markdown.Length + images.Count * 256);
        // CF_HTML requires the fragment to have one outer element.
        fragment.Append("<div>");
        foreach (var line in EnumerateClipboardLines(markdown))
        {
            if (referencesByLineStart.TryGetValue(line.Start, out var reference))
            {
                if (!images.TryGetValue(reference.ImageId, out var image))
                {
                    throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
                }

                string base64;
                if (repeatedImageIds.Contains(reference.ImageId))
                {
                    if (!repeatedBase64.TryGetValue(reference.ImageId, out base64!))
                    {
                        base64 = Convert.ToBase64String(image.Bytes);
                        repeatedBase64.Add(reference.ImageId, base64);
                    }
                }
                else
                {
                    // For one-off images, append the temporary Base64 string and let it die instead
                    // of pinning a second full encoded copy until the whole HTML document is done.
                    base64 = Convert.ToBase64String(image.Bytes);
                }

                fragment.Append("<div><img src=\"data:")
                    .Append(image.Asset.Mime)
                    .Append(";base64,")
                    .Append(base64)
                    .Append("\" alt=\"")
                    .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(reference.Label)
                        ? "image"
                        : reference.Label))
                    .Append('"');
                AppendExternalHtmlImageSizing(fragment, reference.DisplayOptions);
                fragment.Append("></div>");
                continue;
            }

            fragment.Append(line.Text.Length == 0
                ? "<div><br></div>"
                : $"<div style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(line.Text)}</div>");
        }
        fragment.Append("</div>");

        html = BuildCfHtml(fragment);
        return true;
    }

    private static void AppendExternalHtmlImageSizing(
        StringBuilder builder,
        MarkdownImageDisplayOptions options)
    {
        if (options.WidthAttribute is { } width)
        {
            AppendExternalHtmlWidth(builder, width.Value, width.IsPercent);
            return;
        }
        if (options.LabelScalePercent is { } percent)
        {
            AppendExternalHtmlWidth(builder, percent, isPercent: true);
            return;
        }
        if (options.LabelWidth is { } pixels)
        {
            AppendExternalHtmlWidth(builder, pixels, isPercent: false);
            return;
        }

        builder.Append(" style=\"max-width:100%;height:auto\"");
    }

    private static void AppendExternalHtmlWidth(
        StringBuilder builder,
        double value,
        bool isPercent)
    {
        builder.Append(" style=\"max-width:100%;height:auto;width:")
            .Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append(isPercent ? "%\"" : "px\"");
    }

    private static string BuildCfHtml(StringBuilder fragment)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        const string htmlPrefix = "<html><body>" + startMarker;
        const string htmlSuffix = endMarker + "</body></html>";
        const string headerTemplate =
            "Version:1.0\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";

        var placeholderHeader = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            headerTemplate,
            0,
            0,
            0,
            0);
        var startHtml = Encoding.UTF8.GetByteCount(placeholderHeader);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(htmlPrefix);
        var endFragment = checked(startFragment + Utf8ByteCount(fragment));
        var endHtml = checked(endFragment + Encoding.UTF8.GetByteCount(htmlSuffix));
        var header = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            headerTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment);

        var result = new StringBuilder(
            checked(header.Length + htmlPrefix.Length + fragment.Length + htmlSuffix.Length));
        result.Append(header);
        result.Append(htmlPrefix);
        foreach (var chunk in fragment.GetChunks())
        {
            result.Append(chunk.Span);
        }
        result.Append(htmlSuffix);
        return result.ToString();
    }

    private static int Utf8ByteCount(StringBuilder text)
    {
        // Count through the real Encoder state machine instead of per-chunk GetByteCount. A
        // StringBuilder chunk may split a surrogate pair and Encoder.GetByteCount does not advance
        // state between calls; Convert does, while this tiny output buffer avoids another full copy.
        var encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[1024];
        var byteCount = 0;
        foreach (var chunk in text.GetChunks())
        {
            var remaining = chunk.Span;
            while (!remaining.IsEmpty)
            {
                encoder.Convert(
                    remaining,
                    buffer,
                    flush: false,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                byteCount = checked(byteCount + bytesUsed);
                remaining = remaining[charsUsed..];
            }
        }

        var completed = false;
        while (!completed)
        {
            encoder.Convert(
                ReadOnlySpan<char>.Empty,
                buffer,
                flush: true,
                out _,
                out var bytesUsed,
                out completed);
            byteCount = checked(byteCount + bytesUsed);
        }
        return byteCount;
    }

    private static bool TryBuildExternalClipboardRtf(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references,
        IReadOnlyDictionary<string, ClipboardImagePayload> images,
        out string rtf)
    {
        rtf = "";
        long totalSourceBytes = 0;
        foreach (var reference in references)
        {
            if (!images.TryGetValue(reference.ImageId, out var image) ||
                !IsRtfNativeImage(image) ||
                image.Bytes.LongLength <= 0 ||
                totalSourceBytes > MaxExternalClipboardRtfSourceBytes - image.Bytes.LongLength)
            {
                return false;
            }

            totalSourceBytes += image.Bytes.LongLength;
        }

        var extraCapacity = (int)Math.Min(
            int.MaxValue - (long)markdown.Length,
            totalSourceBytes * 2 + 1024);
        var builder = new StringBuilder(markdown.Length + extraCapacity);
        builder.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0 Arial;}}\f0 ");

        var referencesByLineStart = ClipboardReferencesByLineStart(references);
        foreach (var line in EnumerateClipboardLines(markdown))
        {
            if (referencesByLineStart.TryGetValue(line.Start, out var reference))
            {
                if (!images.TryGetValue(reference.ImageId, out var image) ||
                    !TryAppendRtfImage(builder, image, reference.DisplayOptions))
                {
                    return false;
                }
            }
            else
            {
                AppendRtfText(builder, line.Text);
            }

            if (line.HasLineBreak)
            {
                builder.Append(@"\par ");
            }
        }

        builder.Append('}');
        rtf = builder.ToString();
        return true;
    }

    private static bool IsRtfNativeImage(ClipboardImagePayload image)
        => image.Asset.Mime is "image/png" or "image/jpeg";

    private static bool TryAppendRtfImage(
        StringBuilder builder,
        ClipboardImagePayload image,
        MarkdownImageDisplayOptions options)
    {
        var pixelWidth = image.Asset.Width;
        var pixelHeight = image.Asset.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0 || !IsRtfNativeImage(image))
        {
            return false;
        }

        var blip = image.Asset.Mime == "image/png"
            ? @"\pngblip"
            : @"\jpegblip";
        var goalWidthPixels = ExternalClipboardImageGoalWidth(pixelWidth, options);
        var scale = goalWidthPixels / pixelWidth;
        var goalHeightPixels = Math.Max(1, pixelHeight * scale);

        builder.Append(@"{\pict")
            .Append(blip)
            .Append(@"\picw").Append(pixelWidth)
            .Append(@"\pich").Append(pixelHeight)
            .Append(@"\picwgoal").Append(Math.Max(1, (int)Math.Round(goalWidthPixels * 15.0)))
            .Append(@"\pichgoal").Append(Math.Max(1, (int)Math.Round(goalHeightPixels * 15.0)))
            .Append(' ');
        AppendRtfHex(builder, image.Bytes);
        builder.Append('}');
        return true;
    }

    private static void AppendRtfHex(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        const string hexDigits = "0123456789ABCDEF";
        Span<char> buffer = stackalloc char[1024];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var byteCount = Math.Min(bytes.Length - offset, buffer.Length / 2);
            for (var index = 0; index < byteCount; index++)
            {
                var value = bytes[offset + index];
                buffer[index * 2] = hexDigits[value >> 4];
                buffer[index * 2 + 1] = hexDigits[value & 0x0F];
            }

            builder.Append(buffer[..(byteCount * 2)]);
            offset += byteCount;
        }
    }

    private static double ExternalClipboardImageGoalWidth(
        int intrinsicWidth,
        MarkdownImageDisplayOptions options)
    {
        const double defaultMaxWidth = 640;
        if (options.WidthAttribute is { IsPercent: false } width)
        {
            return Math.Clamp(width.Value, 24, defaultMaxWidth);
        }
        if (options.LabelWidth is { } labelWidth)
        {
            return Math.Clamp(labelWidth, 24, defaultMaxWidth);
        }
        if (options.WidthAttribute is { IsPercent: true } percentWidth)
        {
            return Math.Clamp(defaultMaxWidth * percentWidth.Value / 100.0, 24, defaultMaxWidth);
        }
        if (options.LabelScalePercent is { } percent)
        {
            return Math.Clamp(defaultMaxWidth * percent / 100.0, 24, defaultMaxWidth);
        }
        return Math.Clamp(intrinsicWidth, 24, defaultMaxWidth);
    }

    private static void AppendRtfText(StringBuilder builder, string text)
    {
        foreach (var character in text)
        {
            switch (character)
            {
                case '\\': builder.Append(@"\\"); break;
                case '{': builder.Append(@"\{"); break;
                case '}': builder.Append(@"\}"); break;
                case '\t': builder.Append(@"\tab "); break;
                default:
                    if (character is >= ' ' and <= '~')
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append(@"\u")
                            .Append(unchecked((short)character)
                                .ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .Append('?');
                    }
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<int, MarkdownImageReference> ClipboardReferencesByLineStart(
        IReadOnlyList<MarkdownImageReference> references)
    {
        var result = new Dictionary<int, MarkdownImageReference>();
        foreach (var reference in references)
        {
            result[reference.LineStart] = reference;
        }
        return result;
    }

    private static IEnumerable<ClipboardLine> EnumerateClipboardLines(string text)
    {
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n')
            {
                lineEnd++;
            }

            var hasLineBreak = lineEnd < text.Length;
            yield return new ClipboardLine(lineStart, text[lineStart..lineEnd], hasLineBreak);
            if (!hasLineBreak)
            {
                yield break;
            }

            lineStart = lineEnd + 1;
            if (text[lineEnd] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
            {
                lineStart++;
            }
        }
    }

    private static void AddSingleImageClipboardFormat(
        DataObject data,
        string markdown,
        IReadOnlyList<MarkdownImageReference> references,
        IReadOnlyDictionary<string, ClipboardImagePayload> images)
    {
        if (references.Count != 1 ||
            !ClipboardSelectionContainsOnlyImages(markdown, references) ||
            !images.TryGetValue(references[0].ImageId, out var image))
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(image.Bytes, writable: false);
            var frame = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            ClipboardHelper.AddImageFormats(data, frame);
        }
        catch
        {
        }
    }

    private static bool ClipboardSelectionContainsOnlyImages(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references)
    {
        var referencesByLineStart = ClipboardReferencesByLineStart(references);
        foreach (var line in EnumerateClipboardLines(markdown))
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }
            if (!referencesByLineStart.ContainsKey(line.Start))
            {
                return false;
            }
        }
        return true;
    }
}
