using System.IO;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    private readonly record struct SemanticHtmlToken(
        string TagName,
        bool IsClosing,
        int Start,
        int End,
        int DestinationStart,
        int DestinationLength,
        string? Url);

    private static void CollectHtmlSemantics(
        ContainerInline container,
        string source,
        List<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links)
    {
        var stack = new List<SemanticHtmlToken>();
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            if (inline is not HtmlInline html ||
                !TryParseSemanticHtmlToken(html, source, out var token))
            {
                continue;
            }

            if (!token.IsClosing)
            {
                stack.Add(token);
                continue;
            }

            // Be intentionally strict. PaperTodo supports only small, properly nested, single-line
            // inline HTML pairs; malformed or crossed tags remain plain source text.
            if (stack.Count == 0)
            {
                continue;
            }
            if (!string.Equals(stack[^1].TagName, token.TagName, StringComparison.Ordinal))
            {
                stack.Clear();
                continue;
            }

            var opening = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (token.Start <= opening.End)
            {
                continue;
            }

            spans.Add(new MarkdownSemanticSpan(
                MarkdownSemanticSpanKind.HtmlContainer,
                opening.Start,
                token.End - opening.Start));
            spans.Add(new MarkdownSemanticSpan(
                MarkdownSemanticSpanKind.HtmlMarker,
                opening.Start,
                opening.End - opening.Start));
            spans.Add(new MarkdownSemanticSpan(
                MarkdownSemanticSpanKind.HtmlMarker,
                token.Start,
                token.End - token.Start));

            var contentKind = opening.TagName switch
            {
                "b" or "strong" => MarkdownSemanticSpanKind.HtmlStrong,
                "i" or "em" => MarkdownSemanticSpanKind.HtmlEmphasis,
                "s" or "del" => MarkdownSemanticSpanKind.HtmlStrikethrough,
                "u" => MarkdownSemanticSpanKind.HtmlUnderline,
                "code" => MarkdownSemanticSpanKind.HtmlCode,
                _ => (MarkdownSemanticSpanKind?)null
            };
            if (contentKind.HasValue)
            {
                spans.Add(new MarkdownSemanticSpan(
                    contentKind.Value,
                    opening.End,
                    token.Start - opening.End));
            }

            if (opening.TagName == "a" && opening.Url != null)
            {
                links.Add(new MarkdownSemanticLink(
                    opening.Start,
                    token.End - opening.Start,
                    opening.End,
                    token.Start - opening.End,
                    opening.DestinationStart,
                    opening.DestinationLength,
                    opening.Url,
                    false));
            }
        }
    }

    private static bool TryParseSemanticHtmlToken(
        HtmlInline html,
        string source,
        out SemanticHtmlToken token)
    {
        token = default;
        if (!TryNormalizeSourceSpan(html.Span, source.Length, out var start, out var end) ||
            end - start < 3)
        {
            return false;
        }

        var text = source[start..end];
        if (text[0] != '<' || text[^1] != '>')
        {
            return false;
        }

        var index = 1;
        var isClosing = index < text.Length && text[index] == '/';
        if (isClosing)
        {
            index++;
        }

        var nameStart = index;
        while (index < text.Length - 1 && IsSemanticHtmlTagNameChar(text[index]))
        {
            index++;
        }
        if (index == nameStart)
        {
            return false;
        }

        var tagName = text[nameStart..index].ToLowerInvariant();
        if (!IsSupportedSemanticHtmlTag(tagName))
        {
            return false;
        }

        if (isClosing)
        {
            while (index < text.Length - 1 && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            if (index != text.Length - 1)
            {
                return false;
            }

            token = new SemanticHtmlToken(tagName, true, start, end, -1, 0, null);
            return true;
        }

        if (tagName != "a")
        {
            while (index < text.Length - 1 && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            if (index != text.Length - 1)
            {
                return false;
            }

            token = new SemanticHtmlToken(tagName, false, start, end, -1, 0, null);
            return true;
        }

        if (!TryGetSemanticHtmlHref(
                text,
                index,
                start,
                out var normalizedUrl,
                out var destinationStart,
                out var destinationLength))
        {
            return false;
        }

        token = new SemanticHtmlToken(
            tagName,
            false,
            start,
            end,
            destinationStart,
            destinationLength,
            normalizedUrl);
        return true;
    }

    private static bool TryGetSemanticHtmlHref(
        string tagText,
        int index,
        int absoluteTagStart,
        out string normalizedUrl,
        out int destinationStart,
        out int destinationLength)
    {
        normalizedUrl = "";
        destinationStart = -1;
        destinationLength = 0;
        var limit = tagText.Length - 1;

        while (index < limit)
        {
            while (index < limit && char.IsWhiteSpace(tagText[index]))
            {
                index++;
            }
            if (index >= limit)
            {
                break;
            }

            var nameStart = index;
            while (index < limit && IsSemanticHtmlAttributeNameChar(tagText[index]))
            {
                index++;
            }
            if (index == nameStart)
            {
                return false;
            }

            var name = tagText[nameStart..index];
            while (index < limit && char.IsWhiteSpace(tagText[index]))
            {
                index++;
            }
            if (index >= limit || tagText[index] != '=')
            {
                return false;
            }

            index++;
            while (index < limit && char.IsWhiteSpace(tagText[index]))
            {
                index++;
            }
            if (index >= limit)
            {
                return false;
            }

            int valueStart;
            int valueEnd;
            if (tagText[index] is '"' or '\'')
            {
                var quote = tagText[index++];
                valueStart = index;
                while (index < limit && tagText[index] != quote)
                {
                    index++;
                }
                if (index >= limit)
                {
                    return false;
                }
                valueEnd = index++;
            }
            else
            {
                valueStart = index;
                while (index < limit && !char.IsWhiteSpace(tagText[index]))
                {
                    index++;
                }
                valueEnd = index;
            }

            if (!string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawUrl = tagText[valueStart..valueEnd];
            if (!TryNormalizeSemanticHtmlUrl(rawUrl, out normalizedUrl))
            {
                return false;
            }

            destinationStart = absoluteTagStart + valueStart;
            destinationLength = valueEnd - valueStart;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeSemanticHtmlUrl(string rawUrl, out string normalizedUrl)
    {
        normalizedUrl = "";
        var trimmed = rawUrl.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (TryNormalizeSemanticLocalPath(trimmed, out normalizedUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.IsFile)
        {
            return TryNormalizeSemanticLocalPath(uri.LocalPath, out normalizedUrl);
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool TryNormalizeSemanticLocalPath(string rawPath, out string normalizedPath)
    {
        normalizedPath = "";
        var trimmed = rawPath.Trim();
        if (!LooksLikeSemanticLocalPath(trimmed) || IsSemanticDevicePath(trimmed))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(trimmed);
            if (IsSemanticDevicePath(fullPath))
            {
                return false;
            }
            normalizedPath = fullPath;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool LooksLikeSemanticLocalPath(string text) =>
        (text.Length >= 3 &&
         IsSemanticAsciiLetter(text[0]) &&
         text[1] == ':' &&
         IsSemanticDirectorySeparator(text[2])) ||
        (text.Length >= 3 &&
         IsSemanticDirectorySeparator(text[0]) &&
         IsSemanticDirectorySeparator(text[1]) &&
         !IsSemanticDirectorySeparator(text[2]));

    private static bool IsSemanticDevicePath(string text) =>
        text.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        text.StartsWith(@"\\?\", StringComparison.Ordinal);

    private static bool IsSemanticDirectorySeparator(char value) => value is '\\' or '/';

    private static bool IsSemanticAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsSupportedSemanticHtmlTag(string tagName) =>
        tagName is "b" or "strong" or "i" or "em" or "s" or "del" or "u" or "code" or "a";

    private static bool IsSemanticHtmlTagNameChar(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsSemanticHtmlAttributeNameChar(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static void ApplyLegacyCompatibilityBoundaries(
        string source,
        List<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links)
    {
        var multilineHtml = spans
            .Where(span =>
                span.Kind == MarkdownSemanticSpanKind.HtmlContainer &&
                ContainsLineBreak(source, span.Start, span.Length))
            .ToArray();
        if (multilineHtml.Length == 0)
        {
            return;
        }

        foreach (var container in multilineHtml)
        {
            spans.RemoveAll(span =>
                IsHtmlSemanticKind(span.Kind) &&
                span.Start >= container.Start &&
                span.End <= container.End);

            links.RemoveAll(link =>
                link.Start >= container.Start &&
                link.End <= container.End &&
                IsHtmlOpeningAt(source, link.Start));
        }
    }

    private static bool ContainsLineBreak(
        string source,
        int start,
        int length)
    {
        if (length <= 0 || start < 0 || start >= source.Length)
        {
            return false;
        }

        var end = Math.Min(source.Length, start + length);
        for (var index = start; index < end; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsHtmlSemanticKind(MarkdownSemanticSpanKind kind) =>
        kind is MarkdownSemanticSpanKind.HtmlContainer or
            MarkdownSemanticSpanKind.HtmlMarker or
            MarkdownSemanticSpanKind.HtmlStrong or
            MarkdownSemanticSpanKind.HtmlEmphasis or
            MarkdownSemanticSpanKind.HtmlStrikethrough or
            MarkdownSemanticSpanKind.HtmlUnderline or
            MarkdownSemanticSpanKind.HtmlCode;

    private static bool IsHtmlOpeningAt(string source, int start)
    {
        return start >= 0 &&
            start + 2 < source.Length &&
            source[start] == '<' &&
            (source[start + 1] is 'a' or 'A') &&
            (char.IsWhiteSpace(source[start + 2]) || source[start + 2] == '>');
    }
}
