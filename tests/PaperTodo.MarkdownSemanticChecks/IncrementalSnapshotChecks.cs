using System.Runtime.CompilerServices;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal static class IncrementalSnapshotChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckSmallDocumentsUseFullParse();
        CheckLargePlainEditStaysLocal();
        CheckExistingLongFenceExpandsFromSnapshot();
        CheckReferenceDefinitionFallsBack();
        CheckMultilineDeletionContainingReferenceDefinitionFallsBack();
        CheckReferenceUseFallsBack();
        CheckOrdinaryReferenceDocumentEditStaysLocal();
        CheckNewReferenceDefinitionFallsBack();
        CheckNewLongFenceExpandsByStateScan();
        CheckLargeEditPreservesQuoteLevelLocally();
    }

    private static void CheckSmallDocumentsUseFullParse()
    {
        if (MarkdownSemanticDocument.FullParseThresholdChars != 2000)
        {
            throw new InvalidOperationException(
                $"FAIL small-document policy: threshold={MarkdownSemanticDocument.FullParseThresholdChars}");
        }

        var source = "before\n\n```text\n" + new string('x', 1_200) + "\n```\n\nafter\n";
        var document = new TextDocument(source);
        using var semantics = new MarkdownSemanticDocument(document);
        document.Insert(source.IndexOf(new string('x', 20), StringComparison.Ordinal) + 600, "Z");

        if (!semantics.TryGetCurrent(out var actual))
        {
            throw new InvalidOperationException("FAIL small-document policy: no current snapshot");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(document.Text), actual, "small document full parse");
        Console.WriteLine("PASS small documents parse fully below 2K");
    }

    private static void CheckLargePlainEditStaysLocal()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 900; index++)
        {
            builder.Append("plain row ").Append(index).Append(" with ordinary words\n\n");
        }

        var oldSource = builder.ToString();
        var editAt = oldSource.IndexOf("plain row 450", StringComparison.Ordinal) + 10;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);

        if (windowLength < 0 || windowLength > 2_000)
        {
            throw new InvalidOperationException(
                $"FAIL large plain edit: local window unexpectedly large ({windowLength})");
        }
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental))
        {
            throw new InvalidOperationException("FAIL large plain edit: unexpectedly fell back");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, "large plain edit");
        Console.WriteLine($"PASS large plain edit local window={windowLength}");
    }

    private static void CheckExistingLongFenceExpandsFromSnapshot()
    {
        var oldSource = "before\n```csharp\n" + new string('x', 4_000) + "\n```\nafter\n";
        var editAt = oldSource.IndexOf(new string('x', 20), StringComparison.Ordinal) + 2_000;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);

        if (windowLength < 4_000)
        {
            throw new InvalidOperationException(
                $"FAIL existing long fence: old semantic container was not expanded ({windowLength})");
        }
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental))
        {
            throw new InvalidOperationException("FAIL existing long fence: unexpectedly fell back");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, "existing long fence");
        Console.WriteLine($"PASS existing long fence expands window={windowLength}");
    }

    private static void CheckReferenceDefinitionFallsBack()
    {
        var oldSource = BuildReferenceDocument();
        var editAt = oldSource.IndexOf("example.com", StringComparison.Ordinal) + 3;
        var newSource = oldSource.Insert(editAt, "Z");
        AssertFallsBack(oldSource, newSource, "reference definition edit");
    }

    private static void CheckMultilineDeletionContainingReferenceDefinitionFallsBack()
    {
        var builder = new StringBuilder();
        builder.Append("[open][r]\n\n");
        for (var index = 0; index < 700; index++)
        {
            builder.Append("neutral row ").Append(index).Append(" ordinary words\n\n");
        }
        var deleteStart = builder.Length;
        builder.Append("DELETE_ME\n");
        builder.Append("ordinary deleted row\n");
        builder.Append("[r]: mailto:old@example.com\n");
        var deleteEnd = builder.Length;
        builder.Append("tail ordinary row\n");

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(deleteStart, deleteEnd - deleteStart);
        AssertFallsBack(oldSource, newSource, "multiline deletion containing reference definition");
    }

    private static void CheckReferenceUseFallsBack()
    {
        var oldSource = BuildReferenceDocument();
        var editAt = oldSource.IndexOf("target", StringComparison.Ordinal) + 2;
        var newSource = oldSource.Insert(editAt, "Z");
        AssertFallsBack(oldSource, newSource, "reference use edit");
    }

    private static void CheckOrdinaryReferenceDocumentEditStaysLocal()
    {
        var oldSource = BuildReferenceDocument();
        var editAt = oldSource.IndexOf("neutral row 400", StringComparison.Ordinal) + 8;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental))
        {
            throw new InvalidOperationException(
                "FAIL ordinary reference-document edit: unnecessarily fell back");
        }
        var linkOffset = newSource.IndexOf("target", StringComparison.Ordinal);
        if (!incremental.TryGetLinkAtOffset(linkOffset, out var link) ||
            !string.Equals(link.Url, "https://example.com", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "FAIL ordinary reference-document edit: distant resolved reference link was lost");
        }
        Console.WriteLine("PASS ordinary edit stays local and preserves distant reference link");
    }

    private static void CheckNewReferenceDefinitionFallsBack()
    {
        var builder = new StringBuilder();
        builder.Append("[unresolved][new-id]\n\n");
        for (var index = 0; index < 500; index++)
        {
            builder.Append("neutral row ").Append(index).Append("\n\n");
        }
        var oldSource = builder.ToString();
        var anchor = oldSource.IndexOf("neutral row 250", StringComparison.Ordinal);
        var newSource = oldSource.Insert(anchor, "[new-id]: https://example.com/new\n\n");
        AssertFallsBack(oldSource, newSource, "new reference definition");
    }

    private static void CheckNewLongFenceExpandsByStateScan()
    {
        var builder = new StringBuilder();
        builder.Append("before\n\n");
        builder.Append("opening anchor\n\n");
        for (var index = 0; index < 900; index++)
        {
            builder.Append("ordinary body row ").Append(index).Append("\n\n");
        }
        builder.Append("```\n\nafter\n");

        var oldSource = builder.ToString();
        var insertAt = oldSource.IndexOf("opening anchor", StringComparison.Ordinal);
        var newSource = oldSource.Insert(insertAt, "```text\n");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);

        if (windowLength < newSource.Length / 2)
        {
            throw new InvalidOperationException(
                $"FAIL new long fence: fence-state scan did not expand far enough ({windowLength}/{newSource.Length})");
        }
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var local))
        {
            throw new InvalidOperationException(
                "FAIL new long fence: local path unexpectedly fell back");
        }

        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), local, "new long fence state scan");
        Console.WriteLine(
            $"PASS new long fence expands by state scan window={windowLength} full={newSource.Length}");
    }

    private static string BuildReferenceDocument()
    {
        var builder = new StringBuilder();
        builder.Append("[target][id]\n\n");
        for (var index = 0; index < 800; index++)
        {
            builder.Append("neutral row ").Append(index).Append(" ordinary words\n\n");
        }
        builder.Append("[id]: https://example.com\n");
        return builder.ToString();
    }

    private static void AssertFallsBack(string oldSource, string newSource, string name)
    {
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out _))
        {
            throw new InvalidOperationException($"FAIL {name}: local path should decline global reference work");
        }
        Console.WriteLine($"PASS {name} falls back to full parse");
    }

    /// <summary>编辑点远离引用块时，局部解析拼接的 span 需保留每行 QuoteLevel（含惰性续行）。</summary>
    private static void CheckLargeEditPreservesQuoteLevelLocally()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 900; index++)
        {
            builder.Append("plain row ").Append(index).Append(" with ordinary words\n\n");
        }

        // 引用簇放文档末尾，与编辑点远离：外层 + 内层 + 空行 + 外层层内深度1 惰性续行。
        builder.Append("\n> outer one\n>\n> > inner two\n>\n> outer three\nlazy depth1\n");

        var oldSource = builder.ToString();
        var editAt = oldSource.IndexOf("plain row 450", StringComparison.Ordinal) + 10;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental))
        {
            throw new InvalidOperationException(
                "FAIL large quote-preserving edit: unexpectedly fell back to full parse");
        }

        var expected = MarkdownSemanticSnapshot.Parse(newSource);
        AssertEquivalent(expected, incremental, "large edit preserves quote level");

        // 全量语义里关键行必须带正确层级（若局部路径丢了 QuoteLevel，等价断言会先于此处暴露）。
        Console.WriteLine("PASS large edit preserves quote level locally");
    }

    private static void AssertEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual,
        string name)
    {
        if (!SnapshotsEquivalent(expected, actual))
        {
            throw new InvalidOperationException($"FAIL incremental {name}: snapshot mismatch");
        }
    }

    private static bool SnapshotsEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual)
    {
        if (expected.LineCount != actual.LineCount ||
            !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
        {
            return false;
        }

        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)))
            {
                return false;
            }
        }
        return true;
    }
}
