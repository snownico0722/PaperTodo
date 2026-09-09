using PaperTodo;

Run("ATX heading", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("# Heading");
    Equal(1, snapshot.GetLine(0).HeadingLevel, "H1 level");
});

Run("Setext heading", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("Title\n=====");
    Equal(1, snapshot.GetLine(0).HeadingLevel, "Setext content level");
    True(snapshot.GetLine(0).IsSetextHeading, "Setext content trait");
    True(snapshot.GetLine(1).IsSetextMarker, "Setext underline marker");
    Equal(0, snapshot.GetLine(1).HeadingLevel, "Setext marker is not heading content");
});

Run("Lazy block quote", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("> quoted\nlazy continuation");
    True(snapshot.GetLine(0).IsQuoted, "quote opening line");
    True(snapshot.GetLine(1).IsQuoted, "lazy continuation remains in quote");
    Equal(1, snapshot.GetLine(0).QuoteLevel, "quote opening level");
    Equal(1, snapshot.GetLine(1).QuoteLevel, "lazy continuation level");
});

Run("Quote level", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("> a\n> b");
    Equal(1, snapshot.GetLine(0).QuoteLevel, "first quote row");
    Equal(1, snapshot.GetLine(1).QuoteLevel, "second quote row");
});

Run("Nested quote level", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("> a\n> \n> > b\n> \n> c");
    Equal(1, snapshot.GetLine(0).QuoteLevel, "outer row before nest");
    Equal(2, snapshot.GetLine(2).QuoteLevel, "inner nested row");
    Equal(1, snapshot.GetLine(4).QuoteLevel, "outer row after nest");
});

Run("Lazy continuation inside nested quote", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("> a\n> > b\nlazy");
    True(snapshot.GetLine(2).IsQuoted, "deep lazy line stays quoted");
    Equal(2, snapshot.GetLine(2).QuoteLevel, "deep lazy line keeps inner level");
});

Run("Indented code", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("    code\n    second");
    True(snapshot.GetLine(0).IsCode, "first indented code line");
    True(snapshot.GetLine(1).IsCode, "second indented code line");
    False(snapshot.GetLine(0).IsFencedCode, "indented code is not fenced");
});

Run("Fenced code", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("```csharp\nvar x = 1;\n```");
    True(snapshot.GetLine(0).IsFencedCode, "opening fence");
    True(snapshot.GetLine(0).IsFencedCodeOpening, "opening fence boundary");
    True(snapshot.GetLine(1).IsFencedCode, "fenced content");
    False(snapshot.GetLine(1).IsFencedCodeMarker, "fenced content is not a marker");
    True(snapshot.GetLine(2).IsFencedCode, "closing fence");
    True(snapshot.GetLine(2).IsFencedCodeClosing, "closing fence boundary");
});

Run("Four-backtick fence boundaries", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("````\n```\n````");
    True(snapshot.GetLine(0).IsFencedCodeOpening, "four-backtick opening");
    True(snapshot.GetLine(1).IsFencedCode, "shorter inner fence is code content");
    False(snapshot.GetLine(1).IsFencedCodeMarker, "shorter inner fence is not a boundary");
    True(snapshot.GetLine(2).IsFencedCodeClosing, "matching four-backtick closing");
});

Run("Unclosed fence has no closing marker", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("```\ncontent");
    True(snapshot.GetLine(0).IsFencedCodeOpening, "unclosed opening boundary");
    True(snapshot.GetLine(1).IsFencedCode, "unclosed content remains fenced code");
    False(snapshot.GetLine(1).IsFencedCodeClosing, "last content line is not guessed as closing");
});

Run("Thematic break", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("---");
    True(snapshot.GetLine(0).IsHorizontalRule, "thematic break");
});

Run("Ordered list continuation", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("1. first\n   continuation");
    True(
        (snapshot.GetLine(0).Traits & MarkdownSemanticLineTraits.OrderedList) != 0,
        "ordered list first line");
    True(
        (snapshot.GetLine(1).Traits & MarkdownSemanticLineTraits.OrderedList) != 0,
        "ordered list continuation");
    Equal(
        1,
        snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.OrderedListMarker),
        "continuation row has no second marker");
});

Run("List item marker source spans", () =>
{
    const string unorderedSource = "- first";
    var unordered = SingleSpan(
        MarkdownSemanticSnapshot.Parse(unorderedSource),
        MarkdownSemanticSpanKind.UnorderedListMarker);
    Equal("-", unorderedSource.Substring(unordered.Start, unordered.Length), "unordered marker source");

    const string orderedSource = "12) second";
    var ordered = SingleSpan(
        MarkdownSemanticSnapshot.Parse(orderedSource),
        MarkdownSemanticSpanKind.OrderedListMarker);
    Equal("12)", orderedSource.Substring(ordered.Start, ordered.Length), "ordered marker source");
});

Run("Nested quote list marker", () =>
{
    const string source = "> - item";
    var marker = SingleSpan(
        MarkdownSemanticSnapshot.Parse(source),
        MarkdownSemanticSpanKind.UnorderedListMarker);
    Equal(source.IndexOf('-', StringComparison.Ordinal), marker.Start, "nested marker absolute offset");
    Equal("-", source.Substring(marker.Start, marker.Length), "nested marker source");
});

Run("Task list keeps marker domains separate", () =>
{
    const string source = "- [x] done";
    var snapshot = MarkdownSemanticSnapshot.Parse(source);
    var listMarker = SingleSpan(snapshot, MarkdownSemanticSpanKind.UnorderedListMarker);
    var taskMarker = SingleSpan(snapshot, MarkdownSemanticSpanKind.TaskListMarker);
    True(listMarker.End <= taskMarker.Start, "list bullet and task marker do not overlap");
    True(taskMarker.Checked, "task checked state");
});

Run("Task list markers", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("- [ ] todo\n- [x] done");
    var tasks = snapshot.Spans
        .Where(span => span.Kind == MarkdownSemanticSpanKind.TaskListMarker)
        .ToArray();
    Equal(2, tasks.Length, "task marker count");
    False(tasks[0].Checked, "unchecked task state");
    True(tasks[1].Checked, "checked task state");
    True(tasks.All(task => task.Length >= 3), "task marker source spans");
});

Run("Nested quote heading", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("> ## Heading");
    var line = snapshot.GetLine(0);
    True(line.IsQuoted, "nested heading keeps quote semantics");
    Equal(2, line.HeadingLevel, "nested heading level");
});

Run("Strong source span", () =>
{
    const string source = "before **bold** after";
    var span = SingleSpan(MarkdownSemanticSnapshot.Parse(source), MarkdownSemanticSpanKind.Strong);
    Equal(source.IndexOf("**", StringComparison.Ordinal), span.Start, "strong start");
    Equal("**bold**".Length, span.Length, "strong source length");
    Equal(2, span.MarkerLength, "strong marker length");
});

Run("Emphasis source span", () =>
{
    const string source = "a *italic* b";
    var span = SingleSpan(MarkdownSemanticSnapshot.Parse(source), MarkdownSemanticSpanKind.Emphasis);
    Equal(source.IndexOf('*'), span.Start, "emphasis start");
    Equal("*italic*".Length, span.Length, "emphasis source length");
    Equal(1, span.MarkerLength, "emphasis marker length");
});

Run("Strikethrough source span", () =>
{
    const string source = "before ~~gone~~ after";
    var snapshot = MarkdownSemanticSnapshot.Parse(source);
    var span = SingleSpan(snapshot, MarkdownSemanticSpanKind.Strikethrough);
    Equal(source.IndexOf("~~", StringComparison.Ordinal), span.Start, "strikethrough start");
    Equal("~~gone~~".Length, span.Length, "strikethrough source length");
    Equal(2, span.MarkerLength, "strikethrough marker length");
    False(
        snapshot.Spans.Any(candidate => candidate.Kind == MarkdownSemanticSpanKind.Strong),
        "strikethrough must not be misclassified as strong");
});

Run("No extra emphasis extensions", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("~sub~ ++insert++ ==mark== ^super^");
    False(
        snapshot.Spans.Any(span => span.Kind == MarkdownSemanticSpanKind.Strikethrough),
        "single tilde syntax stays disabled");
});

Run("Inline code delimiter span", () =>
{
    const string source = "x ``a`b`` y";
    var span = SingleSpan(MarkdownSemanticSnapshot.Parse(source), MarkdownSemanticSpanKind.InlineCode);
    Equal(source.IndexOf("``", StringComparison.Ordinal), span.Start, "inline code start");
    Equal("``a`b``".Length, span.Length, "inline code source length");
    Equal(2, span.MarkerLength, "inline code delimiter length");
});

Run("Inline link mapping", () =>
{
    const string source = "before [label](https://example.com/a) after";
    var link = SingleLink(MarkdownSemanticSnapshot.Parse(source));
    Equal("https://example.com/a", link.Url, "inline link URL");
    Equal("label", source.Substring(link.LabelStart, link.LabelLength), "inline link label source");
    Equal("https://example.com/a", source.Substring(link.DestinationStart, link.DestinationLength), "inline link destination source");
    False(link.IsAuto, "explicit link is not auto link");
});

Run("Bare web link mapping", () =>
{
    const string source = "see https://example.com/a?q=1 now";
    var link = SingleLink(MarkdownSemanticSnapshot.Parse(source));
    Equal("https://example.com/a?q=1", link.Url, "bare link URL");
    True(link.IsAuto, "bare link uses auto-link semantics");
    Equal("https://example.com/a?q=1", source.Substring(link.LabelStart, link.LabelLength), "bare link label source");
});

Run("Bare HTTP scheme remains case insensitive", () =>
{
    const string source = "see HTTPS://EXAMPLE.COM/A now";
    var link = SingleLink(MarkdownSemanticSnapshot.Parse(source));
    Equal("HTTPS://EXAMPLE.COM/A", source.Substring(link.LabelStart, link.LabelLength), "uppercase bare link source");
    True(link.IsAuto, "uppercase bare link is automatic");
});

Run("Bare protocol scope stays HTTP only", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse(
        "ftp://example.com mailto:test@example.com tel:123456 www.example.com");
    Equal(0, snapshot.Links.Count, "extra AutoLinks protocols stay disabled");
});

Run("Bare link is ignored inside inline code", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("`HTTPS://EXAMPLE.COM/A`");
    Equal(0, snapshot.Links.Count, "inline code must not expose a navigation link");
});

Run("Strong semantics compose inside link label", () =>
{
    const string source = "[**bold**](https://example.com)";
    var snapshot = MarkdownSemanticSnapshot.Parse(source);
    var link = SingleLink(snapshot);
    var strong = SingleSpan(snapshot, MarkdownSemanticSpanKind.Strong);
    Equal("**bold**", source.Substring(link.LabelStart, link.LabelLength), "link label keeps Markdown source span");
    True(strong.Start >= link.LabelStart && strong.End <= link.LabelEnd, "strong span stays inside label");
});

Run("CommonMark angle autolink", () =>
{
    const string source = "<https://example.com>";
    var link = SingleLink(MarkdownSemanticSnapshot.Parse(source));
    Equal("https://example.com", link.Url, "angle autolink URL");
    Equal("https://example.com", source.Substring(link.LabelStart, link.LabelLength), "angle autolink label source");
    True(link.IsAuto, "angle link is automatic");
});

Run("Image is not navigation link", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("![alt](papertodo-image://123)");
    Equal(0, snapshot.Links.Count, "image links stay outside navigation semantics");
});

Run("Escaped emphasis marker", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("\\*not emphasis*");
    False(
        snapshot.Spans.Any(span =>
            span.Kind is MarkdownSemanticSpanKind.Emphasis or MarkdownSemanticSpanKind.Strong),
        "escaped opening marker must not become emphasis");
});

Run("CR-only line mapping", () =>
{
    const string source = "# heading\rplain\r- item";
    var snapshot = MarkdownSemanticSnapshot.Parse(source);
    Equal(3, snapshot.LineCount, "CR-only line count");
    Equal(1, snapshot.GetLine(0).HeadingLevel, "CR-only heading level");
    True((snapshot.GetLine(2).Traits & MarkdownSemanticLineTraits.UnorderedList) != 0, "CR-only list trait");
});

Run("Mixed newline line mapping", () =>
{
    const string source = "# h\r\nplain\r> q\n- x";
    var snapshot = MarkdownSemanticSnapshot.Parse(source);
    Equal(4, snapshot.LineCount, "mixed newline line count");
    True(snapshot.GetLine(2).IsQuoted, "mixed newline quote trait");
    True((snapshot.GetLine(3).Traits & MarkdownSemanticLineTraits.UnorderedList) != 0, "mixed newline list trait");
});

Run("HTTP image destination is not navigation link", () =>
{
    var snapshot = MarkdownSemanticSnapshot.Parse("![alt](https://example.com/a.png)");
    Equal(0, snapshot.Links.Count, "HTTP image destination link count");
    Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.Image), "image exclusion span count");
});

Run("Large document performance smoke", () =>
{
    var builder = new System.Text.StringBuilder(100_000);
    var index = 0;
    while (builder.Length < 98_000)
    {
        builder.Append("# Heading ").Append(index).Append('\n');
        builder.Append("- [ ] item **bold** https://example.com/").Append(index).Append('\n');
        builder.Append("> quote `code` ~~gone~~\n");
        if ((index++ % 20) == 0)
        {
            builder.Append("```csharp\nvar x = 1;\n```\n");
        }
    }

    var source = builder.ToString();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var snapshot = MarkdownSemanticSnapshot.Parse(source);
    stopwatch.Stop();

    True(snapshot.LineCount > 1000, "large document line count");
    True(snapshot.Spans.Count > 1000, "large document semantic spans");
    True(
        stopwatch.Elapsed < TimeSpan.FromSeconds(5),
        $"100k Markdown parse smoke exceeded 5s: {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    Console.WriteLine(
        $"INFO Large document parse {source.Length} chars in {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
});

Console.WriteLine("Markdown semantic checks passed.");
return;

static MarkdownSemanticSpan SingleSpan(
    MarkdownSemanticSnapshot snapshot,
    MarkdownSemanticSpanKind kind)
{
    var matches = snapshot.Spans.Where(span => span.Kind == kind).ToArray();
    Equal(1, matches.Length, $"{kind} span count");
    return matches[0];
}

static MarkdownSemanticLink SingleLink(MarkdownSemanticSnapshot snapshot)
{
    Equal(1, snapshot.Links.Count, "link count");
    return snapshot.Links[0];
}

static void Run(string name, Action check)
{
    try
    {
        check();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"FAIL {name}: {ex.Message}", ex);
    }
}

static void True(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void False(bool value, string message) => True(!value, message);

static void Equal<T>(T expected, T actual, string message)
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
    {
        throw new InvalidOperationException(
            $"{message}: expected {expected}, actual {actual}");
    }
}
