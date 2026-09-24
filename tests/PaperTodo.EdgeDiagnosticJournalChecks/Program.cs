using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PaperTodo;
using JournalBuffer = PaperTodo.EdgeDiagnosticJournal.Buffer;

internal static class Program
{
    private static int _assertions;
    private static bool CollectionEnabledInBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args[0]);
        var root = Path.Combine(Path.GetTempPath(), "papertodo-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            MemoryAndExport(Path.Combine(root, "export"));
            LimitsAndDrops(Path.Combine(root, "limits"));
            ConcurrentOrdering(Path.Combine(root, "parallel"));
            CompletionContention(Path.Combine(root, "contention"));
            FailedFlushCanRetry(Path.Combine(root, "failure"));
            ChildExitScenarios(Path.Combine(root, "child"));
            Console.WriteLine($"Edge diagnostic journal checks: 6/6 groups, {_assertions} assertions passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // This test owns only its newly generated temporary directory.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception(message);
    }

    private static JsonElement[] ReadJournal(string path) => File.ReadLines(path)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();

    private static void MemoryAndExport(string directory)
    {
        var journal = new JournalBuffer(directory, 12, 4096);
        Check(journal.AppendText("edge-preview-performance.log", "scheduler.frame gapMs=16.5"), "Retain existing performance text");
        Check(journal.AppendText("edge-preview-trace.log", "preview open 中文"), "Retain interaction Unicode");
        Check(journal.AppendText("md-render-trace.log", "markdown ready"), "Accept an additional text channel");
        Check(journal.AppendText("../escape.log", "still a journal record"), "Preserve malformed channel as data");
        Check(journal.Event("shape", 12, 1, -2, long.MinValue, long.MaxValue, 0.75, double.NaN, "line\n\"quoted\""), "Retain numeric event without string formatting");
        Check(!Directory.Exists(directory), "Collecting text and events must not create files or directories");
        var entries = journal.Snapshot();
        Check(entries.Length == 5 && entries[4].Value4 == long.MaxValue && entries[4].Number1 == 0.75, "Snapshot retains typed payload");
        entries[0] = default;
        Check(journal.Snapshot()[0].Sequence == 1, "An inspection copy cannot corrupt retained records");
        var complete = journal.Complete("normal-exit");
        Check(complete.Completed && complete.Records == 5 && complete.Dropped == 0, "Normal completion writes every retained event");
        var lines = ReadJournal(complete.Path);
        Check(lines.Length == 7 && lines[0].GetProperty("kind").GetString() == "header" &&
            lines[^1].GetProperty("kind").GetString() == "complete", "JSONL has one session header and completed footer");
        Check(lines[0].GetProperty("processId").GetInt32() == Environment.ProcessId &&
            lines[0].GetProperty("qpcFrequency").GetInt64() == Stopwatch.Frequency, "Session exposes actual process and clock frequency");
        Check(lines[5].GetProperty("detail").GetString() == "line\n\"quoted\"" &&
            lines[5].GetProperty("number2").GetString() == "NaN", "Escaping and nonfinite numeric evidence survive JSONL");
        Check(lines[^1].GetProperty("invalidTextChannels").GetInt32() == 1 &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "escape.log")), "Text channel names cannot escape the output directory");
        Check(lines[^1].GetProperty("allocatedBytesSeal").GetInt64() >= lines[^1].GetProperty("allocatedBytesStart").GetInt64(), "Process allocation checkpoints are ordered");
        var performancePath = Directory.GetFiles(directory, "edge-preview-performance-*.log").Single();
        var text = File.ReadAllText(performancePath);
        Check(text.Contains(" tick=") && text.Contains(" thread=") && text.Contains("scheduler.frame gapMs=16.5") &&
            text.Contains("journalSeq=1"), "Legacy text exports retain messages and add stable sequence metadata");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Successful completion leaves only completed files");
        var bytes = File.ReadAllBytes(complete.Path);
        Check(journal.Complete("process-exit") == complete && bytes.SequenceEqual(File.ReadAllBytes(complete.Path)), "Repeated completion is idempotentent");
        Check(!journal.Event("late") && journal.Stats.RejectedAfterSeal == 1, "Post-exit producers cannot change a sealed capture");
        Console.WriteLine("PASS memory-and-export");
    }

    private static void LimitsAndDrops(string directory)
    {
        var countBounded = new JournalBuffer(Path.Combine(directory, "count"), 2, 1024);
        countBounded.Event("A"); countBounded.Event("B");
        Check(!countBounded.Event("C") && countBounded.Stats.DroppedCapacity == 1 &&
            countBounded.Stats.Attempted == 3, "Full record capacity preserves earlier records and counts drops");
        var textBounded = new JournalBuffer(Path.Combine(directory, "text"), 10, 12);
        Check(textBounded.Event("A", detail: "1234") && !textBounded.Event("B", detail: "1234") && textBounded.Event("C"),
            "Text budget rejects only the oversized addition and can retain a later smaller event");
        var entries = textBounded.Snapshot();
        Check(entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 1, 3 }) &&
            textBounded.Stats.RetainedTextBytes == 12 && textBounded.Stats.DroppedTextBudget == 1,
            "Sequence gaps and independent byte-budget drops are explicit");
        Console.WriteLine("PASS limits-and-drops");
    }

    private static void ConcurrentOrdering(string directory)
    {
        const int workers = 4, records = 1500;
        var journal = new JournalBuffer(directory, workers * records, 1024 * 1024);
        using var barrier = new Barrier(workers);
        var threads = Enumerable.Range(0, workers).Select(worker => new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var index = 0; index < records; index++) journal.Event("parallel", worker, value1: index);
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        var entries = journal.Snapshot();
        Check(entries.Length == workers * records && journal.Stats.DroppedCapacity == 0, "Concurrent producers retain every permitted event");
        Check(entries.Select(entry => entry.ThreadId).Distinct().Count() == workers, "The journal records actual producer thread identity");
        for (var index = 0; index < entries.Length; index++)
        {
            Check(entries[index].Sequence == index + 1, "Cross-thread sequence order is stable");
            if (index > 0) Check(entries[index].Qpc >= entries[index - 1].Qpc, "QPC ordering agrees with cross-thread sequence");
        }
        Check(journal.Complete("parallel-complete").Completed, "Concurrent capture flushes after producers finish");
        Console.WriteLine("PASS concurrent-ordering");
    }

    private static void CompletionContention(string directory)
    {
        foreach (var lockName in new[] { "_gate", "_flushGate" })
        {
            var journal = new JournalBuffer(Path.Combine(directory, lockName), 16, 1024);
            journal.Event("retained");
            var gate = typeof(JournalBuffer).GetField(lockName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!;
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
            holder.Start(); held.Wait();
            try
            {
                var started = Stopwatch.GetTimestamp();
                var result = journal.Complete("unhandled-exception", bestEffort: true);
                Check(!result.Completed && result.Error is "flush-busy" or "record-busy" &&
                    Stopwatch.GetElapsedTime(started).TotalSeconds < 1, "Crash best-effort does not wait for another thread's journal lock");
                Check(!Directory.Exists(Path.Combine(directory, lockName)), "A contended crash flush does not partially publish a complete journal");
            }
            finally { release.Set(); holder.Join(); }
            Check(journal.Complete("normal-exit").Completed, "Normal exit can still flush after the contended best-effort attempt");
        }
        var concurrent = new JournalBuffer(Path.Combine(directory, "complete"), 16, 1024);
        concurrent.Event("one");
        var completions = new EdgeDiagnosticJournal.Completion[2];
        Parallel.For(0, 2, index => completions[index] = concurrent.Complete("concurrent-normal"));
        Check(completions[0] == completions[1] && completions[0].Completed, "Concurrent normal completions join the same completed flush");
        Check(Directory.GetFiles(Path.Combine(directory, "complete"), "*.jsonl").Length == 1, "Concurrent exits do not duplicate the journal");
        Console.WriteLine("PASS completion-contention");
    }

    private static void FailedFlushCanRetry(string directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        File.WriteAllText(directory, "a file blocks creation of the output directory");
        var journal = new JournalBuffer(directory, 16, 1024);
        journal.Event("preserved");
        var failed = journal.Complete("first-attempt");
        Check(!failed.Completed && failed.Error != null && journal.Stats.Sealed, "IO failure seals the capture without throwing into the application");
        Check(!journal.Event("late"), "A failed flush cannot mix later events into the original capture");
        File.Delete(directory);
        var retried = journal.Complete("retry");
        var footer = ReadJournal(retried.Path)[^1];
        Check(retried.Completed && retried.Records == 1 && footer.GetProperty("flushReason").GetString() == "first-attempt" &&
            footer.GetProperty("flushAttemptReason").GetString() == "retry", "Retry retains the original cutoff and records the actual flush reason");
        Check(footer.GetProperty("rejectedAfterSealAtFlush").GetInt64() == 1, "Retry accounts for rejected late producers");
        Console.WriteLine("PASS failed-flush-retry");
    }

    private static int Child(string mode)
    {
        // Release must remain disabled even when its child receives the memory opt-in.
        if (mode == "disabled" || !CollectionEnabledInBuild)
        {
            if (EdgeDiagnosticJournal.Enabled) return 3;
            EdgeDiagnosticJournal.AppendText("edge-preview-performance.log", "disabled");
            EdgeDiagnosticJournal.Event("disabled");
            EdgeDiagnosticJournal.Complete("disabled-exit");
            return 0;
        }
        if (!EdgeDiagnosticJournal.Enabled) return 4;
        if (mode == "uninitialized") { EdgeDiagnosticJournal.Complete("helper-exit"); return 0; }
        EdgeDiagnosticJournal.AppendText("edge-preview-performance.log", "child-event");
        EdgeDiagnosticJournal.Event("child-structured", 1, 2, 3, 4, 5, 0.25, 0.75);
        if (mode == "normal") EdgeDiagnosticJournal.Complete("normal-exit");
        return 0;
    }

    private static void ChildExitScenarios(string directory)
    {
        foreach (var mode in new[] { "disabled", "uninitialized", "normal", "process-exit" })
        {
            var target = Path.Combine(directory, mode);
            var processPath = Environment.ProcessPath!;
            var start = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(mode);
            start.Environment["PAPERTODO_EDGE_DIAGNOSTICS"] = mode == "disabled" ? "" : "memory";
            start.Environment["PAPERTODO_EDGE_DIAGNOSTIC_DIRECTORY"] = target;
            start.Environment["PAPERTODO_EDGE_DIAGNOSTIC_RECORDS"] = "1024";
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10_000)) { process.Kill(); throw new Exception("Diagnostic child failed to exit"); }
            Check(process.ExitCode == 0, "Pure-console diagnostic child exits cleanly: " + process.StandardError.ReadToEnd());
            if (!CollectionEnabledInBuild || mode is "disabled" or "uninitialized")
            {
                Check(!Directory.Exists(target), "Disabled, Release or untouched helper process must not create a diagnostic capture");
                continue;
            }
            var path = Directory.GetFiles(target, "edge-diagnostic-journal-*.jsonl").Single();
            var lines = ReadJournal(path);
            Check(lines[0].GetProperty("processId").GetInt32() == process.Id &&
                lines[^1].GetProperty("count").GetInt32() == 2, "Child exit persists the complete PID-scoped capture");
            Check(lines[^1].GetProperty("flushReason").GetString() == (mode == "normal" ? "normal-exit" : "process-exit"),
                "Explicit normal completion and ProcessExit fallback have distinguishable reasons");
        }
        Console.WriteLine("PASS child-exit-scenarios");
    }
}
