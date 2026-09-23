using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PaperTodo;

/// <summary>
/// Debug-only buffered timings for the complete edge-preview pipeline. UI-thread callers only
/// enqueue a line; a ThreadPool writer batches disk IO so diagnostics cannot become the stall they
/// are intended to measure.
/// </summary>
internal static class EdgeCapsulePerformanceDiagnostics
{
#if DEBUG
    private readonly record struct DiagnosticLine(
        string FileName,
        string Text);

    private readonly record struct TransparentHostResource(
        string DiagnosticId,
        DeviceScreenRect Bounds,
        bool Shown);

    private readonly record struct TransparentHostTotals(
        int RegisteredCount,
        int BoundedCount,
        int ShownCount,
        long PixelArea,
        long RgbaEstimateBytes);

    private readonly record struct ProcessMemorySnapshot(
        long PrivateBytes,
        long WorkingSetBytes,
        long ManagedHeapBytes,
        int HandleCount);

    private const int MaximumQueuedLines = 12_000;
    private const int MaximumFlushBatch = 512;
    private const int ResourceFirstSampleDelayMilliseconds = 150;
    private const int ResourceSettledSampleDelayMilliseconds = 1_850;
    private static readonly ConcurrentQueue<DiagnosticLine> PendingLines = new();
    private static readonly Timer? FlushTimer = EdgeDiagnosticJournal.Enabled ? null : new(
        static _ => FlushPendingLines(),
        null,
        Timeout.Infinite,
        Timeout.Infinite);
    private static readonly object ResourceGate = new();
    private static readonly Dictionary<long, TransparentHostResource>
        TransparentHosts = new();
    private static readonly Timer ResourceSampleTimer = new(
        static _ => SampleTransparentHostResources(),
        null,
        Timeout.Infinite,
        Timeout.Infinite);
    private static int _pendingLineCount;
    private static int _flushScheduled;
    private static long _nextTransparentHostId;
    private static long _resourceGeneration;
    private static int _resourceSamplePhase;
    private static ProcessMemorySnapshot? _resourceBaseline;
#endif

    internal static long Timestamp()
    {
#if DEBUG
        return Stopwatch.GetTimestamp();
#else
        return 0;
#endif
    }

    internal static double ElapsedMilliseconds(long startTimestamp)
    {
#if DEBUG
        return Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
#else
        return 0;
#endif
    }

    internal static double ElapsedMilliseconds(
        long startTimestamp,
        long endTimestamp)
    {
#if DEBUG
        return Stopwatch.GetElapsedTime(startTimestamp, endTimestamp)
            .TotalMilliseconds;
#else
        return 0;
#endif
    }

    internal static string ShortId(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<none>";
        }

        return value[..Math.Min(6, value.Length)];
    }

    internal static long RegisterTransparentHost(string diagnosticId)
    {
#if DEBUG
        ProcessMemorySnapshot? baseline = null;
        lock (ResourceGate)
        {
            if (_resourceBaseline == null)
            {
                baseline = CaptureProcessMemory();
                _resourceBaseline = baseline;
            }
        }

        if (baseline.HasValue)
        {
            TraceResourceSnapshot(
                "baseline-before-first-host",
                generation: 0,
                default,
                baseline.Value,
                baseline.Value);
        }

        var hostId = Interlocked.Increment(ref _nextTransparentHostId);
        TransparentHostTotals totals;
        long generation;
        lock (ResourceGate)
        {
            TransparentHosts[hostId] = new TransparentHostResource(
                diagnosticId,
                default,
                false);
            generation = ScheduleResourceSamplesLocked();
            totals = CalculateTransparentHostTotalsLocked();
        }
        TraceTransparentHost(
            "registered",
            hostId,
            diagnosticId,
            default,
            shown: false,
            totals,
            generation);
        return hostId;
#else
        return 0;
#endif
    }

    internal static void UpdateTransparentHost(
        long hostId,
        string diagnosticId,
        DeviceScreenRect bounds,
        bool shown,
        string reason)
    {
#if DEBUG
        if (hostId <= 0)
        {
            return;
        }

        TransparentHostTotals totals;
        long generation;
        lock (ResourceGate)
        {
            if (!TransparentHosts.TryGetValue(hostId, out var previous) ||
                (previous.Bounds == bounds && previous.Shown == shown))
            {
                return;
            }

            TransparentHosts[hostId] = new TransparentHostResource(
                diagnosticId,
                bounds,
                shown);
            generation = ScheduleResourceSamplesLocked();
            totals = CalculateTransparentHostTotalsLocked();
        }
        TraceTransparentHost(
            reason,
            hostId,
            diagnosticId,
            bounds,
            shown,
            totals,
            generation);
#endif
    }

    internal static void UnregisterTransparentHost(
        long hostId,
        string diagnosticId)
    {
#if DEBUG
        if (hostId <= 0)
        {
            return;
        }

        TransparentHostResource removed;
        TransparentHostTotals totals;
        long generation;
        lock (ResourceGate)
        {
            if (!TransparentHosts.Remove(hostId, out removed))
            {
                return;
            }
            generation = ScheduleResourceSamplesLocked();
            totals = CalculateTransparentHostTotalsLocked();
        }
        TraceTransparentHost(
            "disposed",
            hostId,
            diagnosticId,
            removed.Bounds,
            shown: false,
            totals,
            generation);
#endif
    }

    [Conditional("DEBUG")]
    internal static void Trace(string message)
    {
#if DEBUG
        try
        {
            if (EdgeDiagnosticJournal.Enabled)
            {
                EdgeDiagnosticJournal.AppendText("edge-preview-performance.log", message);
                return;
            }
            Enqueue(
                "edge-preview-performance.log",
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"tick={Stopwatch.GetTimestamp()} " +
                $"thread={Environment.CurrentManagedThreadId} " +
                message);
        }
        catch
        {
        }
#endif
    }

    [Conditional("DEBUG")]
    internal static void TraceInteraction(string message)
    {
#if DEBUG
        try
        {
            if (EdgeDiagnosticJournal.Enabled)
            {
                EdgeDiagnosticJournal.AppendText("edge-preview-trace.log", message);
                return;
            }
            Enqueue(
                "edge-preview-trace.log",
                $"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
        catch
        {
        }
#endif
    }

    [Conditional("DEBUG")]
    internal static void Event(string eventName, long correlationId = 0,
        long value1 = 0, long value2 = 0, long value3 = 0, long value4 = 0,
        double number1 = 0, double number2 = 0, string? detail = null) =>
        EdgeDiagnosticJournal.Event(eventName, correlationId, value1, value2, value3, value4, number1, number2, detail);

#if DEBUG
    private static long ScheduleResourceSamplesLocked()
    {
        var generation = ++_resourceGeneration;
        _resourceSamplePhase = 0;
        try
        {
            ResourceSampleTimer.Change(
                ResourceFirstSampleDelayMilliseconds,
                Timeout.Infinite);
        }
        catch
        {
        }
        return generation;
    }

    private static void SampleTransparentHostResources()
    {
        try
        {
            long generation;
            int phase;
            TransparentHostTotals totals;
            ProcessMemorySnapshot baseline;
            lock (ResourceGate)
            {
                generation = _resourceGeneration;
                phase = _resourceSamplePhase;
                if (phase >= 2)
                {
                    return;
                }
                totals = CalculateTransparentHostTotalsLocked();
                baseline = _resourceBaseline ?? default;
            }

            var memory = CaptureProcessMemory();
            lock (ResourceGate)
            {
                if (_resourceGeneration != generation ||
                    _resourceSamplePhase != phase)
                {
                    return;
                }
                _resourceSamplePhase++;
            }

            TraceResourceSnapshot(
                phase == 0 ? "after-150ms" : "settled-2s",
                generation,
                totals,
                memory,
                baseline);

            if (phase == 0)
            {
                lock (ResourceGate)
                {
                    if (_resourceGeneration == generation &&
                        _resourceSamplePhase == 1)
                    {
                        ResourceSampleTimer.Change(
                            ResourceSettledSampleDelayMilliseconds,
                            Timeout.Infinite);
                    }
                }
            }
        }
        catch
        {
            // Resource diagnostics must never affect the window lifecycle.
        }
    }

    private static TransparentHostTotals CalculateTransparentHostTotalsLocked()
    {
        var boundedCount = 0;
        var shownCount = 0;
        long pixelArea = 0;
        foreach (var resource in TransparentHosts.Values)
        {
            if (resource.Shown)
            {
                shownCount++;
            }
            if (resource.Bounds.IsEmpty)
            {
                continue;
            }
            boundedCount++;
            pixelArea += (long)resource.Bounds.Width * resource.Bounds.Height;
        }
        return new TransparentHostTotals(
            TransparentHosts.Count,
            boundedCount,
            shownCount,
            pixelArea,
            pixelArea * 4);
    }

    private static ProcessMemorySnapshot CaptureProcessMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMemorySnapshot(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            process.HandleCount);
    }

    private static void TraceTransparentHost(
        string action,
        long hostId,
        string diagnosticId,
        DeviceScreenRect bounds,
        bool shown,
        TransparentHostTotals totals,
        long generation)
    {
        var pixels = bounds.IsEmpty
            ? 0
            : (long)bounds.Width * bounds.Height;
        Trace(
            $"resource.host action={action} generation={generation} " +
            $"hostId={hostId} paper={diagnosticId} shown={shown} " +
            $"bounds={(bounds.IsEmpty ? "<none>" : $"{bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}")} " +
            $"pixels={pixels} rgbaEstimateMiB={ToMiB(pixels * 4):F3} " +
            $"registered={totals.RegisteredCount} bounded={totals.BoundedCount} " +
            $"shownTotal={totals.ShownCount} totalPixels={totals.PixelArea} " +
            $"totalRgbaEstimateMiB={ToMiB(totals.RgbaEstimateBytes):F3}");
    }

    private static void TraceResourceSnapshot(
        string phase,
        long generation,
        TransparentHostTotals totals,
        ProcessMemorySnapshot memory,
        ProcessMemorySnapshot baseline)
    {
        Trace(
            $"resource.snapshot phase={phase} generation={generation} " +
            "scope=process-memory excludesDwmGpu=true " +
            $"registered={totals.RegisteredCount} bounded={totals.BoundedCount} " +
            $"shown={totals.ShownCount} totalPixels={totals.PixelArea} " +
            $"rgbaEstimateMiB={ToMiB(totals.RgbaEstimateBytes):F3} " +
            $"privateMiB={ToMiB(memory.PrivateBytes):F3} " +
            $"privateDeltaMiB={ToMiB(memory.PrivateBytes - baseline.PrivateBytes):F3} " +
            $"workingSetMiB={ToMiB(memory.WorkingSetBytes):F3} " +
            $"workingSetDeltaMiB={ToMiB(memory.WorkingSetBytes - baseline.WorkingSetBytes):F3} " +
            $"managedHeapMiB={ToMiB(memory.ManagedHeapBytes):F3} " +
            $"managedHeapDeltaMiB={ToMiB(memory.ManagedHeapBytes - baseline.ManagedHeapBytes):F3} " +
            $"handles={memory.HandleCount} handleDelta={memory.HandleCount - baseline.HandleCount}");
    }

    private static double ToMiB(long bytes) =>
        bytes / (1024.0 * 1024.0);

    private static void Enqueue(string fileName, string line)
    {
        if (Interlocked.Increment(ref _pendingLineCount) > MaximumQueuedLines)
        {
            Interlocked.Decrement(ref _pendingLineCount);
            return;
        }

        PendingLines.Enqueue(new DiagnosticLine(fileName, line));
        ScheduleFlush();
    }

    private static void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Let one short burst accumulate before touching disk. Animation and pointer work can
            // therefore enqueue dozens of detailed timings while the writer performs one append.
            FlushTimer?.Change(50, Timeout.Infinite);
        }
        catch
        {
            Volatile.Write(ref _flushScheduled, 0);
        }
    }

    private static void FlushPendingLines()
    {
        try
        {
            var batch = new List<DiagnosticLine>(MaximumFlushBatch);
            while (batch.Count < MaximumFlushBatch &&
                   PendingLines.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _pendingLineCount);
                batch.Add(line);
            }

            if (batch.Count > 0)
            {
                foreach (var group in batch.GroupBy(
                             line => line.FileName,
                             StringComparer.Ordinal))
                {
                    var path = Path.Combine(
                        AppContext.BaseDirectory,
                        group.Key);
                    File.AppendAllLines(
                        path,
                        group.Select(line => line.Text),
                        Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Performance diagnostics must never affect preview availability or animation.
        }
        finally
        {
            Volatile.Write(ref _flushScheduled, 0);
            if (!PendingLines.IsEmpty)
            {
                ScheduleFlush();
            }
        }
    }
#endif
}
