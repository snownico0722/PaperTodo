using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

internal readonly record struct NoteTextCounts(int Lines, int Body, int Source);

// One immutable source-coordinate index per text revision. Selection queries do not
// parse fragments: a URL or formatting marker stays non-body text even when selected alone.
internal sealed class NoteTextStatistics
{
    private readonly int _length;
    private readonly int[] _lineStarts;
    private readonly int[] _bodyPositions;
    private readonly int[] _sourcePositions;

    private NoteTextStatistics(int length, int[] lineStarts, int[] body, int[] source)
    {
        _length = length;
        _lineStarts = lineStarts;
        _bodyPositions = body;
        _sourcePositions = source;
        Total = new(lineStarts.Length, body.Length, source.Length);
    }

    internal NoteTextCounts Total { get; }
    internal static int Count(string text) => Create(text).Total.Body;

    internal NoteTextCounts Select(int start, int length)
    {
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        if (length == 0) return default;
        if (start == 0 && length == _length) return Total;
        var end = start + length;
        // A selected trailing newline belongs to its preceding line, rather than
        // selecting an extra empty line. The full document includes its final empty line.
        var lines = LowerBound(_lineStarts, end) - LowerBound(_lineStarts, start + 1) + 1;
        return new(lines, InRange(_bodyPositions, start, end), InRange(_sourcePositions, start, end));
    }

    internal static NoteTextStatistics Create(string text)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lineStarts.Add(i + 1);
        }
        var body = new List<int>();
        var source = new List<int>();
        AddRunes(text.AsSpan(), 0, source);
        var excluded = new List<(int Start, int End)>();
        Visit(MarkdownSemanticSnapshot.ParseDocument(text));
        body.Sort();
        // Image ranges are disjoint and in document order, so filtering stays linear.
        var range = 0;
        var filtered = new List<int>(body.Count);
        foreach (var position in body)
        {
            while (range < excluded.Count && excluded[range].End <= position) range++;
            if (range == excluded.Count || position < excluded[range].Start) filtered.Add(position);
        }
        return new(text.Length, lineStarts.ToArray(), filtered.ToArray(), source.ToArray());

        void AddSource(int start, int end)
        {
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            AddRunes(text.AsSpan(start, end - start), start, body);
        }

        void Visit(MarkdownObject node)
        {
            switch (node)
            {
                case LiteralInline literal:
                    AddSource(literal.Span.Start + (literal.IsFirstCharacterEscaped ? 1 : 0), literal.Span.End + 1);
                    break;
                case CodeInline code:
                    AddSource(code.Span.Start + code.DelimiterCount, code.Span.End + 1 - code.DelimiterCount);
                    break;
                case AutolinkInline link:
                    AddSource(link.Span.Start + 1, link.Span.End);
                    break;
                case HtmlEntityInline entity:
                    // A decoded entity's characters belong to its first source position.
                    foreach (var rune in entity.Transcoded.ToString().EnumerateRunes())
                        if (!Rune.IsWhiteSpace(rune)) body.Add(entity.Span.Start);
                    break;
                case LeafBlock block when block is CodeBlock or HtmlBlock:
                    // Unsupported block HTML remains literal text in the editor.
                    for (var i = 0; i < block.Lines.Count; i++)
                    {
                        var line = block.Lines.Lines[i];
                        // Markdig may expand indentation tabs into a temporary slice.
                        // Its non-whitespace content still ends at the physical line end.
                        var end = line.Position;
                        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
                        var content = line.Slice.AsSpan().TrimStart();
                        AddSource(end - content.Length, end);
                    }
                    break;
                case ContainerBlock container:
                    foreach (var child in container) Visit(child);
                    break;
                case LeafBlock leaf when leaf.Inline != null:
                    Visit(leaf.Inline);
                    break;
                case ContainerInline container:
                    if (container is LinkInline { IsImage: true } image) ExcludeImageOptions(image);
                    for (var child = container.FirstChild; child != null; child = child.NextSibling) Visit(child);
                    break;
            }
        }

        void ExcludeImageOptions(LinkInline image)
        {
            var line = Math.Max(0, LowerBound(lineStarts, image.Span.Start + 1) - 1);
            var start = lineStarts[line];
            var end = line + 1 < lineStarts.Count ? lineStarts[line + 1] : text.Length;
            if (!MarkdownImageReferences.TryParseReferenceLine(text[start..end], out var reference)) return;
            var separator = reference.Label.IndexOf('|');
            if (separator >= 0 && (reference.DisplayOptions.LabelWidth != null ||
                                   reference.DisplayOptions.LabelScalePercent != null))
                excluded.Add((image.Span.Start + 2 + separator, image.Span.Start + 2 + reference.Label.Length));
            excluded.Add((image.Span.End + 1, end));
        }
    }

    private static void AddRunes(ReadOnlySpan<char> text, int start, List<int> positions)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)) positions.Add(start);
            start += rune.Utf16SequenceLength;
        }
    }

    private static int InRange(int[] positions, int start, int end)
        => LowerBound(positions, end) - LowerBound(positions, start);

    private static int LowerBound(IReadOnlyList<int> values, int value)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
