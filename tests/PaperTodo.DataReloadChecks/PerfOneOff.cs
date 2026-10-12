using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    private static string FormatPerf(IEnumerable<double> measurements)
    {
        var samples = measurements.OrderBy(n => n).ToArray();
        var p50 = samples[(samples.Length - 1) / 2];
        var p95 = samples[Math.Max(0, (int)Math.Ceiling(samples.Length * 0.95) - 1)];
        return $"samples={samples.Length} min_ms={samples[0]:F2} median_ms={p50:F2} p95_ms={p95:F2} max_ms={samples[^1]:F2}";
    }

    private static void RunPerformanceChecks()
    {
        foreach (int noteCount in new[] { 1, 10, 50 })
        {
            var root = Path.Combine(Path.GetTempPath(), "PaperTodo.ReloadPerf." + Guid.NewGuid().ToString("N"));
            var plainDir = Path.Combine(root, "plain");
            var trackedDir = Path.Combine(root, "tracked");
            Directory.CreateDirectory(plainDir);
            Directory.CreateDirectory(trackedDir);
            try
            {
                var state = NewState();
                state.Papers[0].Content = new string('a', 80000);
                for (int i = 1; i < noteCount; i++)
                    state.Papers.Add(new PaperData
                    {
                        Id = "perf-note-" + i, Type = PaperTypes.Note,
                        IsVisible = false, Content = new string((char)('a' + i % 24), 80000)
                    });

                var plain = new StateStore(plainDir, DurableAtomicFileWriter.Shared);
                var tracked = new StateStore(trackedDir, DurableAtomicFileWriter.Shared);
                plain.SaveJsonSync(plain.SerializeState(state), 1);
                tracked.SaveJsonSync(tracked.SerializeState(state), 1);
                var plainMemory = plain.Load();
                var trackedMemory = tracked.Load();
                tracked.EnableExternalReload(trackedMemory);
                plain.SaveJsonSync(plain.SerializeState(plainMemory), 2);
                tracked.SaveJsonSync(tracked.SerializeState(trackedMemory), 2);

                var baselineTimes = new List<double>();
                var trackedTimes = new List<double>();
                for (int i = 0; i < 12; i++)
                {
                    int revision = 3 + i;
                    var start = Stopwatch.GetTimestamp();
                    plain.SaveJsonSync(plain.SerializeState(plainMemory), revision);
                    baselineTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    start = Stopwatch.GetTimestamp();
                    tracked.SaveJsonSync(tracked.SerializeState(trackedMemory), revision);
                    trackedTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                var reloadTimes = new List<double>();
                for (int i = 0; i < 7; i++)
                {
                    var document = JsonNode.Parse(File.ReadAllText(tracked.FilePath))!.AsObject();
                    document["papers"]![0]!["title"] = "external-perf-" + i;
                    File.WriteAllText(tracked.FilePath, document.ToJsonString());
                    var started = Stopwatch.GetTimestamp();
                    var result = tracked.ReloadExternalState(trackedMemory, 15 + i);
                    Require(result.Result.Applied && result.State != null, "Performance reload rejected: " + result.Result.Outcome);
                    StateReloadModels.Apply(trackedMemory, result.State);
                    reloadTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                Console.WriteLine($"PERF_STORE notes={noteCount} bytes={new FileInfo(tracked.FilePath).Length} plain_{FormatPerf(baselineTimes)}");
                Console.WriteLine($"PERF_STORE notes={noteCount} bytes={new FileInfo(tracked.FilePath).Length} tracked_{FormatPerf(trackedTimes)}");
                Console.WriteLine($"PERF_STORE notes={noteCount} bytes={new FileInfo(tracked.FilePath).Length} reload_and_model_{FormatPerf(reloadTimes)}");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
