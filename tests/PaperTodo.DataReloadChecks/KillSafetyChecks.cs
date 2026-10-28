using System.Diagnostics;
using System.IO;
using PaperTodo;

internal static partial class Program
{
    private static void KillSafetyChecks()
    {
        foreach (var merge in new[] { false, true })
        foreach (var stage in Enum.GetValues<DurableAtomicWriteStage>())
        {
            using var s = new Scope();
            File.WriteAllText(Path.Combine(s.DirectoryPath, ".kill-fixture"), "isolated");
            Require(s.Store.TryRefreshBackupFromPrimary(), "kill fixture backup");
            var original = File.ReadAllBytes(s.Store.FilePath);
            var backup = File.ReadAllBytes(s.Store.BackupPath);
            var next = s.Store.Load();
            next.Papers[0].Content = new string('X', 100_000) + " complete replacement";
            var nextJson = s.Store.SerializeState(next);
            File.WriteAllText(Path.Combine(s.DirectoryPath, "candidate-input.txt"), nextJson);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PaperTodo.DataReloadChecks.exe"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, CreateNoWindow = true
            };
            foreach (var arg in new[] { "--kill-writer", s.DirectoryPath, stage.ToString(), merge.ToString() })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            try
            {
                var ready = process.StandardOutput.ReadLineAsync();
                Require(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result == "KILL_READY",
                    "child failed to reach kill boundary " + stage);
                process.Kill(entireProcessTree: true);
                Require(process.WaitForExit(20_000), "killed writer did not exit");
                var actual = File.ReadAllBytes(s.Store.FilePath);
                var expected = stage == DurableAtomicWriteStage.AfterReplace
                    ? System.Text.Encoding.UTF8.GetBytes(nextJson) : original;
                Require(actual.SequenceEqual(expected), $"{merge}/{stage}: partial or incorrect generation");
                Require(File.ReadAllBytes(s.Store.BackupPath).SequenceEqual(backup), "kill damaged independent backup");
                var recovered = new StateStore(s.DirectoryPath, DurableAtomicFileWriter.Shared).Load();
                Require(recovered.Papers[0].Content == (stage == DurableAtomicWriteStage.AfterReplace
                    ? next.Papers[0].Content : "original"), "restart recovered incorrect content");
                if (merge)
                {
                    var archives = Directory.GetFiles(s.DirectoryPath, "dataconflict-*.json");
                    Require(archives.Length == 2, "conflict commit did not flush exactly its existing archive pair");
                    var diff = J(File.ReadAllText(archives.Single(p => p.EndsWith(".diff.json"))));
                    Require(Str(diff["conflicts"]![0]!["paperTodo"]) == next.Papers[0].Content,
                        "killed merge lost the local recovery value");
                }
                Console.WriteLine($"PASS real process kill: merge={merge}, stage={stage}; exact generation + backup + restart");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            }
        }
    }

    private static int RunKillWriter(string[] args)
    {
        try
        {
            var directory = args[1];
            if (File.ReadAllText(Path.Combine(directory, ".kill-fixture")) != "isolated") return 2;
            var stage = Enum.Parse<DurableAtomicWriteStage>(args[2]);
            var merge = bool.Parse(args[3]);
            var primary = Path.Combine(directory, "data.json");
            var writer = new DurableAtomicFileWriter((current, path) =>
            {
                if (current != stage || path != primary) return;
                Console.WriteLine("KILL_READY"); Console.Out.Flush();
                Console.ReadLine(); // parent kills us; no finally/Dispose is allowed to "help"
                throw new InvalidOperationException("kill protocol returned unexpectedly");
            });
            var store = new StateStore(directory, writer);
            var initial = store.Load();
            var candidateJson = File.ReadAllText(Path.Combine(directory, "candidate-input.txt"));
            if (!merge) store.SaveJsonSync(candidateJson, 2);
            else
            {
                // Deliberately create a content conflict, then choose the complete local value.
                var baseline = J(File.ReadAllText(primary)); baseline["papers"]![0]!["content"] = "older baseline";
                var local = System.Text.Json.JsonSerializer.Deserialize<AppState>(candidateJson, StateStore.JsonOptions)!;
                var preview = store.PreviewExternalMerge(local, 2, baseline.ToJsonString());
                store.CommitExternalMerge(ResolvePreview(preview, ConflictChoice.KeepMemory), preview.ObservedDiskJson!, 2);
            }
            return 3;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
