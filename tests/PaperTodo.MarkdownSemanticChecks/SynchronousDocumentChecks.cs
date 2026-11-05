using System.Runtime.CompilerServices;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal static class SynchronousDocumentChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckPublicationIsSynchronous();
        CheckBatchedChangePublishesFinalSemantics();
        CheckSynchronousFullFallback();
    }

    private static void CheckPublicationIsSynchronous()
    {
        var document = new TextDocument("title\nplain");
        using var semantics = new MarkdownSemanticDocument(document);
        var publications = 0;
        semantics.SnapshotChanged += _ => publications++;

        document.Insert(0, "# ");

        if (publications != 1 ||
            !semantics.TryGetCurrent(out var snapshot) ||
            snapshot.GetLine(0).HeadingLevel != 1)
        {
            throw new InvalidOperationException(
                "FAIL synchronous semantic publication was not current when TextDocument.Insert returned.");
        }

        Console.WriteLine("PASS synchronous semantic publication before document mutation returns");
    }

    private static void CheckBatchedChangePublishesFinalSemantics()
    {
        var document = new TextDocument("item");
        using var semantics = new MarkdownSemanticDocument(document);
        var publications = 0;
        semantics.SnapshotChanged += _ => publications++;

        document.BeginUpdate();
        try
        {
            document.Insert(0, "- ");
            document.Insert(document.TextLength, " **bold**");
        }
        finally
        {
            document.EndUpdate();
        }

        if (publications != 1 || !semantics.TryGetCurrent(out var snapshot))
        {
            throw new InvalidOperationException(
                $"FAIL batched synchronous semantic publication count={publications}.");
        }

        AssertEquivalentToFullParse(
            document.Text,
            snapshot,
            "batched synchronous semantics");
        Console.WriteLine("PASS batched synchronous semantic publication matches final source");
    }

    private static void CheckSynchronousFullFallback()
    {
        var source =
            "[shared]: https://example.com/a\n\n" +
            BuildOrdinary98k() +
            "\n[far link][shared]\n";
        var editAt = source.IndexOf("example.com/a", StringComparison.Ordinal) +
            "example.com/".Length;
        if (editAt < "example.com/".Length)
        {
            throw new InvalidOperationException("FAIL synchronous full-fallback fixture probe missing.");
        }

        var document = new TextDocument(source);
        using var semantics = new MarkdownSemanticDocument(document);
        var publications = 0;
        semantics.SnapshotChanged += _ => publications++;

        document.Insert(editAt, "z");

        if (publications != 1 || !semantics.TryGetCurrent(out var snapshot))
        {
            throw new InvalidOperationException(
                $"FAIL synchronous full-fallback publication count={publications}.");
        }

        AssertEquivalentToFullParse(
            document.Text,
            snapshot,
            "synchronous full fallback");
        Console.WriteLine("PASS synchronous full fallback publishes exact semantics before return");
    }


    private static void AssertEquivalentToFullParse(
        string source,
        MarkdownSemanticSnapshot actual,
        string name)
    {
        var expected = MarkdownSemanticSnapshot.Parse(source);
        if (expected.LineCount != actual.LineCount ||
            !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException($"FAIL {name} does not match a full parse.");
        }

        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)) ||
                !expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)) ||
                !expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
            {
                throw new InvalidOperationException(
                    $"FAIL {name} line semantics differ at {line}.");
            }
        }
    }

    private static string BuildOrdinary98k()
    {
        var builder = new StringBuilder(100_000);
        var index = 0;
        while (builder.Length < 98_000)
        {
            builder.Append("Plain paragraph ").Append(index)
                .Append(" contains ordinary editable words and enough neutral content for profiling.\n\n");
            if ((index++ % 17) == 0)
            {
                builder.Append("A **bold** token and https://example.com/path nearby.\n\n");
            }
        }
        return builder.ToString();
    }
}
