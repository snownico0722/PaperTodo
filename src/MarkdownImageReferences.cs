using System.Text;
using System.Globalization;

namespace PaperTodo;

public readonly record struct MarkdownImageReference(
    string ImageId,
    int LineStart,
    int LineLength,
    string Label,
    string AttributeText,
    MarkdownImageDisplayOptions DisplayOptions)
{
    public string WithUrl(string url)
        => $"![{Label}]({url}){AttributeText}";
}

public readonly record struct MarkdownImageDisplayOptions(
    double? LabelWidth,
    double? LabelScalePercent,
    MarkdownImageWidthAttribute? WidthAttribute);

public readonly record struct MarkdownImageWidthAttribute(double Value, bool IsPercent);

public static class MarkdownImageReferences
{
    public const string UriPrefix = "i:";
    // Legacy v2.2 render marker. New editors never create it; keep this only for migration cleanup.
    public const char RenderMarker = '\u2060';
    public const string RenderMarkerText = "\u2060";

    public static string CreateReference(string imageId)
        => $"![image|100%]({UriPrefix}{imageId})";

    public static bool TryParseLine(string line, out string imageId)
    {
        imageId = "";
        if (!TryParseReferenceLine(line, out var reference))
        {
            return false;
        }

        imageId = reference.ImageId;
        return true;
    }

    public static bool TryParseReferenceLine(string line, out MarkdownImageReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("![", StringComparison.Ordinal) ||
            !TrySplitMarkdownImage(trimmed, out var label, out var url, out var attributeText))
        {
            return false;
        }

        if (!url.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = url[UriPrefix.Length..].Trim();
        if (!IsValidImageId(id))
        {
            return false;
        }

        reference = new MarkdownImageReference(
            id,
            0,
            line.Length,
            label,
            attributeText,
            ParseDisplayOptions(label, attributeText));
        return true;
    }

    public static HashSet<string> CollectImageIds(string? markdown)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in Enumerate(markdown))
        {
            ids.Add(reference.ImageId);
        }

        return ids;
    }

    public static bool IsRenderMarkerLine(string line)
        => string.Equals(line.Trim(), RenderMarkerText, StringComparison.Ordinal);

    public static string StripRenderMarkers(string text)
    {
        if (text.IndexOf(RenderMarker) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n')
            {
                lineEnd++;
            }

            var line = text[lineStart..lineEnd];
            var delimiterEnd = lineEnd;
            if (delimiterEnd < text.Length)
            {
                delimiterEnd++;
                if (text[lineEnd] == '\r' && delimiterEnd < text.Length && text[delimiterEnd] == '\n')
                {
                    delimiterEnd++;
                }
            }

            if (!IsRenderMarkerLine(line))
            {
                builder.Append(line.Replace(RenderMarkerText, ""));
                builder.Append(text, lineEnd, delimiterEnd - lineEnd);
            }

            if (delimiterEnd >= text.Length)
            {
                break;
            }

            lineStart = delimiterEnd;
        }

        return builder.ToString();
    }

    public static IEnumerable<MarkdownImageReference> Enumerate(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            yield break;
        }

        var fencedCodeState = default(MarkdownFencedCodeState);
        var lineStart = 0;
        while (lineStart <= markdown.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < markdown.Length && markdown[lineEnd] is not '\r' and not '\n')
            {
                lineEnd++;
            }

            var line = markdown[lineStart..lineEnd];
            var fenceKind = MarkdownFencedCodeScanner.ClassifyLine(
                line,
                fencedCodeState,
                out var nextFencedCodeState);
            var wasInsideFencedCodeBlock = fencedCodeState.IsInside;
            fencedCodeState = nextFencedCodeState;
            if (fenceKind == MarkdownFenceLineKind.None &&
                !wasInsideFencedCodeBlock &&
                TryParseReferenceLine(line, out var reference))
            {
                yield return reference with
                {
                    LineStart = lineStart,
                    LineLength = lineEnd - lineStart
                };
            }

            if (lineEnd >= markdown.Length)
            {
                yield break;
            }

            lineStart = lineEnd + 1;
            if (markdown[lineEnd] == '\r' && lineStart < markdown.Length && markdown[lineStart] == '\n')
            {
                lineStart++;
            }
        }
    }

    public static string ReplaceForExternalMarkdown(
        string markdown,
        Func<string, string?> externalPathForImageId)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (var reference in Enumerate(markdown))
        {
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            var externalPath = externalPathForImageId(reference.ImageId);
            builder.Append(externalPath == null
                ? markdown.Substring(reference.LineStart, reference.LineLength)
                : reference.WithUrl(externalPath));
            cursor = reference.LineStart + reference.LineLength;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private static bool TrySplitMarkdownImage(
        string text,
        out string label,
        out string url,
        out string attributeText)
    {
        label = "";
        url = "";
        attributeText = "";

        var labelEnd = text.IndexOf("](", 2, StringComparison.Ordinal);
        if (labelEnd < 0)
        {
            return false;
        }

        var urlStart = labelEnd + 2;
        var urlEnd = text.IndexOf(')', urlStart);
        if (urlEnd < 0 || urlEnd <= urlStart)
        {
            return false;
        }

        var tail = text[(urlEnd + 1)..].Trim();
        if (tail.Length > 0 &&
            !(tail.StartsWith("{", StringComparison.Ordinal) && tail.EndsWith("}", StringComparison.Ordinal)))
        {
            return false;
        }

        label = text[2..labelEnd];
        url = text[urlStart..urlEnd].Trim();
        attributeText = tail;
        return true;
    }

    private static MarkdownImageDisplayOptions ParseDisplayOptions(string label, string attributeText)
    {
        var labelWidth = (double?)null;
        var labelScalePercent = (double?)null;

        var pipe = label.IndexOf('|');
        if (pipe >= 0 && pipe + 1 < label.Length)
        {
            var specs = label[(pipe + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var spec in specs)
            {
                if (TryParseDimensions(spec, out var width, out _))
                {
                    labelWidth = width;
                    continue;
                }

                if (TryParsePercent(spec, out var percent))
                {
                    labelScalePercent = percent;
                }
            }
        }

        return new MarkdownImageDisplayOptions(
            labelWidth,
            labelScalePercent,
            TryParseWidthAttribute(attributeText, out var widthAttribute) ? widthAttribute : null);
    }

    private static bool TryParseDimensions(string value, out double width, out double height)
    {
        width = 0;
        height = 0;
        var normalized = value.Trim().Replace('×', 'x');
        var separator = normalized.IndexOf('x');
        if (separator <= 0 || separator >= normalized.Length - 1)
        {
            return false;
        }

        return TryParsePositiveNumber(normalized[..separator], out width) &&
            TryParsePositiveNumber(normalized[(separator + 1)..], out height);
    }

    private static bool TryParsePercent(string value, out double percent)
    {
        percent = 0;
        var trimmed = value.Trim();
        if (!trimmed.EndsWith("%", StringComparison.Ordinal))
        {
            return false;
        }

        return TryParsePositiveNumber(trimmed[..^1], out percent);
    }

    private static bool TryParseWidthAttribute(string attributeText, out MarkdownImageWidthAttribute widthAttribute)
    {
        widthAttribute = default;
        var trimmed = attributeText.Trim();
        if (trimmed.Length < 3 ||
            !trimmed.StartsWith("{", StringComparison.Ordinal) ||
            !trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return false;
        }

        var body = trimmed[1..^1].Trim();
        if (body.Length == 0)
        {
            return false;
        }

        var parts = body.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
            {
                separator = part.IndexOf(':');
            }

            if (separator <= 0 || separator >= part.Length - 1)
            {
                continue;
            }

            var name = part[..separator].Trim();
            if (!string.Equals(name, "width", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[(separator + 1)..].Trim();
            var isPercent = value.EndsWith("%", StringComparison.Ordinal);
            if (isPercent)
            {
                value = value[..^1];
            }
            else if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^2];
            }

            if (TryParsePositiveNumber(value, out var parsed))
            {
                widthAttribute = new MarkdownImageWidthAttribute(parsed, isPercent);
                return true;
            }
        }

        return false;
    }

    private static bool TryParsePositiveNumber(string value, out double number)
    {
        number = 0;
        if (!double.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ||
            double.IsNaN(parsed) ||
            double.IsInfinity(parsed) ||
            parsed <= 0)
        {
            return false;
        }

        number = parsed;
        return true;
    }

    public static bool IsValidImageId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length is < 3 or > 8)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is >= '0' and <= '9')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
