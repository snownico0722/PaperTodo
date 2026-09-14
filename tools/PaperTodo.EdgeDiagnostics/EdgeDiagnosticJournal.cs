using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PaperTodo;

// Kept BCL-only so historical diagnostic builds can use exactly the same observer. Collection
// never opens a file, starts a timer, subscribes Rendering, or invokes an application callback.
internal static class EdgeDiagnosticJournal
{
    private const int DefaultCapacity = 131_072;
    private const int DefaultTextMiB = 32;
#if DEBUG
    private static readonly bool MemoryMode = string.Equals(
        Environment.GetEnvironmentVariable("PAPERTODO_EDGE_DIAGNOSTICS"), "memory", StringComparison.OrdinalIgnoreCase);
    private static readonly Lazy<Buffer?> Current = new(CreateBuffer, LazyThreadSafetyMode.ExecutionAndPublication);
    private static string? _initializationError;
#endif

    internal static bool Enabled
    {
        get
        {
#if DEBUG
            return MemoryMode;
#else
            return false;
#endif
        }
    }

    [Conditional("DEBUG")]
    internal static void AppendText(string fileName, string message)
    {
#if DEBUG
        if (!Enabled) return;
        try { Current.Value?.AppendText(fileName, message); }
        catch { /* Optional diagnostics cannot break their observed operation. */ }
#endif
    }

    [Conditional("DEBUG")]
    internal static void Event(string eventName, long correlationId = 0,
        long value1 = 0, long value2 = 0, long value3 = 0, long value4 = 0,
        double number1 = 0, double number2 = 0, string? detail = null)
    {
#if DEBUG
        if (!Enabled) return;
        try { Current.Value?.Event(eventName, correlationId, value1, value2, value3, value4, number1, number2, detail); }
        catch { /* Collection failure never changes presentation or input. */ }
#endif
    }

    // Call after application/controller cleanup so teardown remains observable. This path is
    // synchronous; a normal exit does not rely on a ThreadPool writer surviving Environment.Exit.
    [Conditional("DEBUG")]
    internal static void Complete(string reason)
    {
#if DEBUG
        if (!Enabled || !Current.IsValueCreated) return;
        try
        {
            if (Current.Value is { } buffer) buffer.Complete(reason);
            else if (_initializationError != null)
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,
                    $"edge-diagnostic-init-failed-{Environment.ProcessId}.json"),
                    JsonSerializer.Serialize(new { reason, error = _initializationError }));
        }
        catch { /* Read-only/full storage must not prevent exit. */ }
#endif
    }

#if DEBUG
    private static Buffer? CreateBuffer()
    {
        if (!Enabled) return null;
        try
        {
            var directory = Environment.GetEnvironmentVariable("PAPERTODO_EDGE_DIAGNOSTIC_DIRECTORY");
            var buffer = new Buffer(string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory,
                ReadLimit("PAPERTODO_EDGE_DIAGNOSTIC_RECORDS", DefaultCapacity, 1024, 1_048_576),
                (long)ReadLimit("PAPERTODO_EDGE_DIAGNOSTIC_TEXT_MIB", DefaultTextMiB, 1, 256) * 1024 * 1024);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => buffer.Complete("process-exit", bestEffort: true);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.IsTerminating) buffer.Complete("unhandled-exception", bestEffort: true);
            };
            return buffer;
        }
        catch (Exception ex)
        {
            _initializationError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static int ReadLimit(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum) : fallback;
#endif

    internal readonly record struct Entry(long Sequence, long Qpc, int ThreadId, bool IsText,
        string Name, string? Detail, long CorrelationId, long Value1, long Value2, long Value3, long Value4,
        double Number1, double Number2);

    internal readonly record struct Statistics(int Count, long Attempted, long DroppedCapacity,
        long DroppedTextBudget, long RejectedAfterSeal, long RetainedTextBytes,
        long EventCalls, long EventTicks, bool Sealed);

    internal readonly record struct Completion(bool Completed, string Path, string Reason, int Records,
        long Dropped, string? Error);

    // Instance surface exists for independent executable behavior checks, with no static mode,
    // WPF/Application dependency, process hooks, hidden worker, or test-only flush callback.
    internal sealed class Buffer
    {
        private readonly object _gate = new();
        private readonly object _flushGate = new();
        private readonly Entry[] _entries;
        private readonly long _maximumTextBytes;
        private readonly string _directory;
        private readonly long _allocatedAtStart;
        private readonly int[] _collectionsAtStart;
        private int _count;
        private long _attempted;
        private long _droppedCapacity;
        private long _droppedTextBudget;
        private long _rejectedAfterSeal;
        private long _retainedTextBytes;
        private long _eventCalls;
        private long _eventTicks;
        private bool _sealed;
        private bool _completed;
        private string? _flushReason;
        private long _sealedQpc;
        private long _allocatedAtSeal;
        private int[]? _collectionsAtSeal;
        private Statistics _sealedStatistics;
        private Completion _completion;
        private int _invalidTextChannels;

        internal Buffer(string directory, int capacity = DefaultCapacity, long maximumTextBytes = DefaultTextMiB * 1024L * 1024L)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maximumTextBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumTextBytes));
            _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            _collectionsAtStart = CollectionCounts();
            StartUtc = DateTimeOffset.UtcNow;
            StartQpc = Stopwatch.GetTimestamp();
            LocalOffset = TimeZoneInfo.Local.GetUtcOffset(StartUtc);
            ProcessId = Environment.ProcessId;
            SessionId = ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            _directory = Path.GetFullPath(directory);
            _maximumTextBytes = maximumTextBytes;
            _entries = new Entry[capacity];
        }

        internal string SessionId { get; }
        internal int ProcessId { get; }
        internal long StartQpc { get; }
        internal DateTimeOffset StartUtc { get; }
        internal TimeSpan LocalOffset { get; }
        internal string OutputPath => Path.Combine(_directory, "edge-diagnostic-journal-" + SessionId + ".jsonl");
        internal Statistics Stats { get { lock (_gate) return GetStatistics(); } }

        internal bool AppendText(string fileName, string message) =>
            Append(true, fileName, message, 0, 0, 0, 0, 0, 0, 0);

        internal bool Event(string eventName, long correlationId = 0,
            long value1 = 0, long value2 = 0, long value3 = 0, long value4 = 0,
            double number1 = 0, double number2 = 0, string? detail = null) =>
            Append(false, eventName, detail, correlationId, value1, value2, value3, value4, number1, number2);

        private bool Append(bool isText, string name, string? detail, long correlationId,
            long value1, long value2, long value3, long value4, double number1, double number2)
        {
            ArgumentNullException.ThrowIfNull(name);
            // Structured observer cost includes contention and the recording work up to releasing
            // this lock, excluding Monitor.Exit and the public facade. Three QPC reads, no sampling.
            var eventStart = isText ? 0 : Stopwatch.GetTimestamp();
            lock (_gate)
            {
                try
                {
                    if (_sealed) { _rejectedAfterSeal++; return false; }
                    _attempted++;
                    if (!isText) _eventCalls++;
                    if (_count == _entries.Length) { _droppedCapacity++; return false; }
                    var textBytes = ((long)name.Length + (detail?.Length ?? 0)) * sizeof(char);
                    if (textBytes > _maximumTextBytes - _retainedTextBytes)
                    {
                        _droppedTextBudget++;
                        return false;
                    }
                    // QPC is acquired under the same short lock as sequence assignment, so the
                    // retained cross-thread journal is globally ordered even under contention.
                    _entries[_count++] = new Entry(_attempted, Stopwatch.GetTimestamp(),
                        Environment.CurrentManagedThreadId, isText, name, detail, correlationId,
                        value1, value2, value3, value4, number1, number2);
                    _retainedTextBytes += textBytes;
                    return true;
                }
                finally
                {
                    if (!isText && !_sealed) _eventTicks += Stopwatch.GetTimestamp() - eventStart;
                }
            }
        }

        internal Entry[] Snapshot()
        {
            lock (_gate)
            {
                var entries = new Entry[_count];
                Array.Copy(_entries, entries, _count);
                return entries;
            }
        }

        private Statistics GetStatistics() => new(_count, _attempted, _droppedCapacity,
            _droppedTextBudget, _rejectedAfterSeal, _retainedTextBytes, _eventCalls, _eventTicks, _sealed);

        internal Completion Complete(string reason, bool bestEffort = false)
        {
            // UnhandledException may run while an unrelated thread owns one of these locks.
            // The crash path never waits for it. Normal exit joins an already running flush.
            var flushEntered = false;
            try
            {
                if (bestEffort)
                {
                    if (!Monitor.TryEnter(_flushGate)) return new(false, OutputPath, reason, 0, 0, "flush-busy");
                    flushEntered = true;
                }
                else Monitor.Enter(_flushGate, ref flushEntered);
                if (_completed) return _completion;

                var recordEntered = false;
                try
                {
                    if (bestEffort)
                    {
                        if (!Monitor.TryEnter(_gate)) return new(false, OutputPath, reason, 0, 0, "record-busy");
                        recordEntered = true;
                    }
                    else Monitor.Enter(_gate, ref recordEntered);
                    if (!_sealed)
                    {
                        _sealed = true;
                        _flushReason = reason;
                        _sealedQpc = Stopwatch.GetTimestamp();
                        _allocatedAtSeal = GC.GetTotalAllocatedBytes(precise: false);
                        _collectionsAtSeal = CollectionCounts();
                        _sealedStatistics = GetStatistics();
                    }
                }
                finally { if (recordEntered) Monitor.Exit(_gate); }

                var started = Stopwatch.GetTimestamp();
                Directory.CreateDirectory(_directory);
                // Each process/session owns its files, including text exports; an exit command
                // helper cannot truncate, append into, or take the file lock of the GUI journal.
                WriteCompatibleText();
                WriteJournal(started, reason);
                _completed = true;
                return _completion = new(true, OutputPath, _flushReason!, _sealedStatistics.Count,
                    _sealedStatistics.DroppedCapacity + _sealedStatistics.DroppedTextBudget, null);
            }
            catch (Exception ex)
            {
                // Preserve sealed entries for a later normal/process-exit retry. A partial .tmp
                // is visibly incomplete; only the successfully closed journal gets its final name.
                return _completion = new(false, OutputPath, _flushReason ?? reason, _count,
                    _droppedCapacity + _droppedTextBudget, ex.GetType().Name + ": " + ex.Message);
            }
            finally { if (flushEntered) Monitor.Exit(_flushGate); }
        }

        private void WriteCompatibleText()
        {
            _invalidTextChannels = 0;
            foreach (var group in _entries.Take(_count).Where(entry => entry.IsText)
                         .GroupBy(entry => entry.Name, StringComparer.Ordinal))
            {
                // Channels are filenames, never paths supplied by a log message.
                if (group.Key.IndexOfAny(new[] { '/', '\\', ':', '\r', '\n' }) >= 0 ||
                    group.Key is "" or "." or ".." || group.Key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    // Keep the event in the authoritative journal, but never interpret its
                    // arbitrary channel as a filesystem path or lose unrelated valid records.
                    _invalidTextChannels++;
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(group.Key) + "-" + SessionId + Path.GetExtension(group.Key);
                var path = Path.Combine(_directory, name);
                using (var writer = new StreamWriter(path + ".tmp", false, new UTF8Encoding(false), 64 * 1024))
                {
                    foreach (var entry in group)
                    {
                        var time = StartUtc.ToOffset(LocalOffset).AddSeconds((entry.Qpc - StartQpc) / (double)Stopwatch.Frequency);
                        writer.Write(time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                        if (group.Key == "edge-preview-performance.log")
                        {
                            writer.Write(" tick="); writer.Write(entry.Qpc.ToString(CultureInfo.InvariantCulture));
                            writer.Write(" thread="); writer.Write(entry.ThreadId.ToString(CultureInfo.InvariantCulture));
                        }
                        writer.Write(' '); writer.Write(entry.Detail);
                        writer.Write(" journalSeq="); writer.Write(entry.Sequence.ToString(CultureInfo.InvariantCulture));
                        writer.Write(" pid="); writer.WriteLine(ProcessId.ToString(CultureInfo.InvariantCulture));
                    }
                }
                File.Move(path + ".tmp", path, overwrite: true);
            }
        }

        private void WriteJournal(long flushStarted, string attemptReason)
        {
            var path = OutputPath;
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "header"); writer.WriteNumber("schema", 1);
                writer.WriteString("sessionId", SessionId); writer.WriteNumber("processId", ProcessId);
                writer.WriteNumber("qpcFrequency", Stopwatch.Frequency); writer.WriteNumber("startQpc", StartQpc);
                writer.WriteString("startUtc", StartUtc); writer.WriteNumber("localOffsetMinutes", LocalOffset.TotalMinutes);
                writer.WriteNumber("capacity", _entries.Length); writer.WriteNumber("maximumTextBytes", _maximumTextBytes);
                writer.WriteNumber("entrySizeBytes", Unsafe.SizeOf<Entry>());
                writer.WriteString("timestampScope", "QPC and sequence acquired together; wall clock reconstructed from session anchor");
                writer.WriteString("observerScope", "all structured Event calls including lock contention; excludes Monitor.Exit, facade and legacy text formatting");
                writer.WriteString("memoryScope", "process allocated bytes and GC collections; includes journal buffer; excludes DWM/GPU");
                writer.WriteEndObject(); FinishLine(writer, stream);
                for (var index = 0; index < _count; index++)
                {
                    var entry = _entries[index];
                    writer.WriteStartObject();
                    writer.WriteString("kind", entry.IsText ? "text" : "event");
                    writer.WriteNumber("seq", entry.Sequence); writer.WriteNumber("qpc", entry.Qpc);
                    writer.WriteNumber("thread", entry.ThreadId); writer.WriteString("name", entry.Name);
                    if (!entry.IsText)
                    {
                        writer.WriteNumber("correlationId", entry.CorrelationId);
                        writer.WriteNumber("value1", entry.Value1); writer.WriteNumber("value2", entry.Value2);
                        writer.WriteNumber("value3", entry.Value3); writer.WriteNumber("value4", entry.Value4);
                        WriteNumber(writer, "number1", entry.Number1); WriteNumber(writer, "number2", entry.Number2);
                    }
                    writer.WriteString("detail", entry.Detail);
                    writer.WriteEndObject(); FinishLine(writer, stream);
                }
                writer.WriteStartObject();
                writer.WriteString("kind", "complete"); writer.WriteString("flushReason", _flushReason);
                writer.WriteString("flushAttemptReason", attemptReason); writer.WriteNumber("sealedQpc", _sealedQpc);
                writer.WriteNumber("count", _sealedStatistics.Count); writer.WriteNumber("attempted", _sealedStatistics.Attempted);
                writer.WriteNumber("droppedCapacity", _sealedStatistics.DroppedCapacity);
                writer.WriteNumber("droppedTextBudget", _sealedStatistics.DroppedTextBudget);
                writer.WriteNumber("rejectedAfterSealAtFlush", Interlocked.Read(ref _rejectedAfterSeal));
                writer.WriteNumber("invalidTextChannels", _invalidTextChannels);
                writer.WriteNumber("retainedTextBytes", _sealedStatistics.RetainedTextBytes);
                writer.WriteNumber("eventCalls", _sealedStatistics.EventCalls); writer.WriteNumber("eventTicks", _sealedStatistics.EventTicks);
                writer.WriteNumber("allocatedBytesStart", _allocatedAtStart); writer.WriteNumber("allocatedBytesSeal", _allocatedAtSeal);
                writer.WritePropertyName("gcCollectionsStart"); JsonSerializer.Serialize(writer, _collectionsAtStart);
                writer.WritePropertyName("gcCollectionsSeal"); JsonSerializer.Serialize(writer, _collectionsAtSeal);
                writer.WriteNumber("flushTicksBeforeFooter", Stopwatch.GetTimestamp() - flushStarted);
                writer.WriteString("flushTimingScope", "through footer start; excludes footer, final filesystem flush and rename");
                writer.WriteEndObject(); FinishLine(writer, stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
        }

        private static int[] CollectionCounts() => new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        private static void FinishLine(Utf8JsonWriter writer, Stream stream)
        {
            writer.Flush(); stream.WriteByte((byte)'\n'); writer.Reset(stream);
        }
        private static void WriteNumber(Utf8JsonWriter writer, string name, double value)
        {
            if (double.IsFinite(value)) writer.WriteNumber(name, value);
            else writer.WriteString(name, value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
