using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PaperTodo;

internal readonly record struct MarkdownParseStageProfile(
    double LineStartsMs,
    double MarkdigParseMs,
    double CollectBlocksMs,
    double CompatibilityMs,
    double EscapeMs,
    double BareHttpMs,
    double SortMs,
    double ApplySpanToLinesMs,
    double SpanLineIndexMs,
    double LinkLineIndexMs,
    double FinalizeSnapshotMs,
    double TotalMs);

internal sealed partial class MarkdownSemanticSnapshot
{
    internal static (MarkdownSemanticSnapshot Snapshot, MarkdownParseStageProfile Profile) ProfileParseForTests(
        string? markdown)
    {
        var totalStart = Stopwatch.GetTimestamp();
        var source = markdown ?? string.Empty;

        var stageStart = Stopwatch.GetTimestamp();
        var lineStarts = BuildLineStarts(source);
        var lineStartsMs = ElapsedMilliseconds(stageStart);

        var spans = new List<MarkdownSemanticSpan>();
        var links = new List<MarkdownSemanticLink>();

        stageStart = Stopwatch.GetTimestamp();
        var document = Markdig.Markdown.Parse(source, Pipeline);
        var markdigParseMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        CollectBlocks(document, source, lineStarts, spans, links);
        var collectBlocksMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        ApplyLegacyCompatibilityBoundaries(source, spans, links);
        var compatibilityMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        CollectEscapeMarkers(source, spans);
        var escapeMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        CollectBareHttpLinks(source, spans, links);
        var bareHttpMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        spans.Sort(CompareSemanticSpans);
        links.Sort(CompareSemanticLinks);
        var sortMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var lines = new MarkdownSemanticLine[lineStarts.Length];
        foreach (var span in spans)
        {
            ApplySpanToLines(source, lineStarts, lines, span);
        }
        var applySpanToLinesMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var spansByLine = BuildSpanLineIndex(source, lineStarts, spans);
        var spanLineIndexMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var linksByLine = BuildLinkLineIndex(source, lineStarts, links);
        var linkLineIndexMs = ElapsedMilliseconds(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var snapshot = new MarkdownSemanticSnapshot(
            lineStarts,
            lines,
            spans.ToArray(),
            links.ToArray(),
            spansByLine,
            linksByLine);
        var finalizeSnapshotMs = ElapsedMilliseconds(stageStart);

        var totalMs = ElapsedMilliseconds(totalStart);
        return (
            snapshot,
            new MarkdownParseStageProfile(
                lineStartsMs,
                markdigParseMs,
                collectBlocksMs,
                compatibilityMs,
                escapeMs,
                bareHttpMs,
                sortMs,
                applySpanToLinesMs,
                spanLineIndexMs,
                linkLineIndexMs,
                finalizeSnapshotMs,
                totalMs));
    }

    private static double ElapsedMilliseconds(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}

internal static class PerformanceProfileChecks
{
    private const int ProfileIterations = 7;

    internal static string BuildLargeStressSource()
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

        return builder.ToString();
    }

    [ModuleInitializer]
    internal static void RunDetailedParseProfile()
    {
        var source = BuildLargeStressSource();

        // Warm the exact profiling path so JIT and first-use package initialization do not dominate.
        GC.KeepAlive(MarkdownSemanticSnapshot.ProfileParseForTests(source).Snapshot);
        GC.KeepAlive(MarkdownSemanticSnapshot.ProfileParseForTests(source).Snapshot);

        var profiles = new MarkdownParseStageProfile[ProfileIterations];
        MarkdownSemanticSnapshot? lastSnapshot = null;
        for (var index = 0; index < profiles.Length; index++)
        {
            var result = MarkdownSemanticSnapshot.ProfileParseForTests(source);
            lastSnapshot = result.Snapshot;
            profiles[index] = result.Profile;
        }

        if (lastSnapshot == null || lastSnapshot.LineCount <= 1000 || lastSnapshot.Spans.Count <= 1000)
        {
            throw new InvalidOperationException("Detailed parse profile did not produce the expected stress snapshot.");
        }

        var median = profiles
            .OrderBy(static profile => profile.TotalMs)
            .ElementAt(profiles.Length / 2);

        Console.WriteLine(
            $"PROFILE Markdown semantic parse {source.Length} chars; median-total run of {ProfileIterations} warmed runs");
        Print("BuildLineStarts", median.LineStartsMs);
        Print("Markdig.Parse", median.MarkdigParseMs);
        Print("CollectBlocks", median.CollectBlocksMs);
        Print("Compatibility", median.CompatibilityMs);
        Print("Escape", median.EscapeMs);
        Print("BareHttp", median.BareHttpMs);
        Print("Sort", median.SortMs);
        Print("ApplySpanToLines", median.ApplySpanToLinesMs);
        Print("SpanLineIndex", median.SpanLineIndexMs);
        Print("LinkLineIndex", median.LinkLineIndexMs);
        Print("FinalizeSnapshot", median.FinalizeSnapshotMs);
        Print("TOTAL", median.TotalMs);

        ProfileDenseEscapes();
    }

    private static void ProfileDenseEscapes()
    {
        var source = string.Concat(Enumerable.Repeat("\\*", 15_000));
        GC.KeepAlive(MarkdownSemanticSnapshot.Parse(source));
        GC.KeepAlive(MarkdownSemanticSnapshot.Parse(source));

        var elapsed = new double[ProfileIterations];
        for (var index = 0; index < elapsed.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var escapeCount = snapshot.Spans.Count(
                static span => span.Kind == MarkdownSemanticSpanKind.EscapeMarker);
            if (escapeCount != 15_000)
            {
                throw new InvalidOperationException(
                    $"Dense escape profile expected 15000 markers, actual {escapeCount}.");
            }
            elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(elapsed);
        Console.WriteLine(
            $"PROFILE DenseEscapes30k p50={elapsed[elapsed.Length / 2]:F3}ms " +
            $"p95={elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1]:F3}ms");
    }

    private static void Print(string stage, double milliseconds) =>
        Console.WriteLine($"PROFILE {stage,-20} {milliseconds,8:F3} ms");
}
