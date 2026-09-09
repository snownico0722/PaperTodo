using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PaperTodo;

internal readonly record struct MarkdownIncrementalStageProfile(
    double DiffMs,
    double WindowMs,
    double LocalParseMs,
    double SpliceMs,
    double LineStartsMs,
    double LinesMs,
    double SpanIndexMs,
    double LinkIndexMs,
    double FinalizeMs,
    double TotalMs,
    int WindowLength,
    int SpanCount,
    int LinkCount);

internal sealed partial class MarkdownSemanticSnapshot
{
    internal static int GetIncrementalWindowLengthForTests(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource)
    {
        FindContiguousDifference(
            oldSource,
            newSource,
            out var changedStart,
            out var oldChangedEnd,
            out var newChangedEnd);
        var delta = newSource.Length - oldSource.Length;
        return TryBuildIncrementalWindow(
            oldSource,
            oldSnapshot,
            newSource,
            changedStart,
            oldChangedEnd,
            newChangedEnd,
            delta,
            out _,
            out _,
            out var newStart,
            out var newEnd)
            ? newEnd - newStart
            : -1;
    }

    internal static (MarkdownSemanticSnapshot Snapshot, MarkdownIncrementalStageProfile Profile)
        ProfileIncrementalForTests(
            string oldSource,
            MarkdownSemanticSnapshot oldSnapshot,
            string newSource)
    {
        var totalStart = Stopwatch.GetTimestamp();
        var stageStart = totalStart;

        FindContiguousDifference(
            oldSource,
            newSource,
            out var changedStart,
            out var oldChangedEnd,
            out var newChangedEnd);
        var diffMs = ElapsedMs(stageStart);
        var delta = newSource.Length - oldSource.Length;

        stageStart = Stopwatch.GetTimestamp();
        if (!TryBuildIncrementalWindow(
                oldSource,
                oldSnapshot,
                newSource,
                changedStart,
                oldChangedEnd,
                newChangedEnd,
                delta,
                out var oldStart,
                out var oldEnd,
                out var newStart,
                out var newEnd))
        {
            throw new InvalidOperationException("Dense incremental profile failed to build its local window.");
        }
        var windowMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var local = Parse(newSource[newStart..newEnd]);
        var localParseMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var spans = SpliceSpans(oldSnapshot._spans, local._spans, oldStart, oldEnd, newStart, delta);
        var links = SpliceLinks(oldSnapshot._links, local._links, oldStart, oldEnd, newStart, delta);
        var spliceMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var lineStarts = BuildLineStarts(newSource);
        var lineStartsMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var lines = new MarkdownSemanticLine[lineStarts.Length];
        foreach (var span in spans)
        {
            ApplySpanToLines(newSource, lineStarts, lines, span);
        }
        var linesMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var spansByLine = BuildSpanLineIndex(newSource, lineStarts, spans);
        var spanIndexMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var linksByLine = BuildLinkLineIndex(newSource, lineStarts, links);
        var linkIndexMs = ElapsedMs(stageStart);

        stageStart = Stopwatch.GetTimestamp();
        var snapshot = new MarkdownSemanticSnapshot(
            lineStarts,
            lines,
            spans,
            links,
            spansByLine,
            linksByLine);
        var finalizeMs = ElapsedMs(stageStart);

        return (
            snapshot,
            new MarkdownIncrementalStageProfile(
                diffMs,
                windowMs,
                localParseMs,
                spliceMs,
                lineStartsMs,
                linesMs,
                spanIndexMs,
                linkIndexMs,
                finalizeMs,
                ElapsedMs(totalStart),
                newEnd - newStart,
                spans.Length,
                links.Length));
    }

    private static double ElapsedMs(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

internal static class IncrementalProfileChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var oldSource = PerformanceProfileChecks.BuildLargeStressSource();
        var probe = oldSource.IndexOf("- [ ] item ", oldSource.Length / 2, StringComparison.Ordinal);
        var editAt = probe + "- [ ] item ".Length;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        GC.KeepAlive(MarkdownSemanticSnapshot.ProfileIncrementalForTests(oldSource, oldSnapshot, newSource).Snapshot);
        GC.KeepAlive(MarkdownSemanticSnapshot.ProfileIncrementalForTests(oldSource, oldSnapshot, newSource).Snapshot);

        const int iterations = 9;
        var profiles = new MarkdownIncrementalStageProfile[iterations];
        MarkdownSemanticSnapshot? snapshot = null;
        for (var index = 0; index < iterations; index++)
        {
            var result = MarkdownSemanticSnapshot.ProfileIncrementalForTests(oldSource, oldSnapshot, newSource);
            snapshot = result.Snapshot;
            profiles[index] = result.Profile;
        }

        GC.KeepAlive(snapshot);
        var median = profiles.OrderBy(static profile => profile.TotalMs).ElementAt(iterations / 2);
        Console.WriteLine(
            $"PROFILE Incremental dense stages window={median.WindowLength} spans={median.SpanCount} links={median.LinkCount}");
        Print("Diff", median.DiffMs);
        Print("Window", median.WindowMs);
        Print("LocalParse", median.LocalParseMs);
        Print("Splice", median.SpliceMs);
        Print("BuildLineStarts", median.LineStartsMs);
        Print("ApplyLines", median.LinesMs);
        Print("SpanLineIndex", median.SpanIndexMs);
        Print("LinkLineIndex", median.LinkIndexMs);
        Print("Finalize", median.FinalizeMs);
        Print("TOTAL", median.TotalMs);
    }

    private static void Print(string name, double ms) =>
        Console.WriteLine($"PROFILE Incremental.{name,-16} {ms,8:F3} ms");
}
