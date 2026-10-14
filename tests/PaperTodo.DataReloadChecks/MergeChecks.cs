using System.IO;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    private static void MergeIndependentEdits()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Title = "local";
        var disk = s.Read();
        disk["papers"]![0]!["content"] = "external";
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2);
        Require(preview.State != null && preview.Result.Outcome == "pending" &&
                preview.Conflicts is { Count: 0 } &&
                preview.State.Papers[0].Title == "local" &&
                preview.State.Papers[0].Content == "external",
                "independent edits were not retained");
        var archived = s.Store.CommitExternalMerge(preview.State, preview.ObservedDiskJson!, 2, preview.Conflicts);
        Require(archived.ConflictFile == null && archived.ConflictDiffFile == null,
                "clean merge generated conflict files");
        Require(Str(s.Read()["papers"]![0]!["title"]) == "local" &&
                Str(s.Read()["papers"]![0]!["content"]) == "external",
                "accepted merge was not written to primary");
        Require(!File.ReadAllText(s.Store.FilePath).Contains("$paperTodoRevision", StringComparison.Ordinal),
                "opt-in merge added obsolete persistent revision tracking");
        Require(s.Store.PreviewExternalMerge(preview.State, 3).Result.Outcome == "unchanged",
                "accepted source must become the new in-memory baseline");
    }

    private static void MergePaperConflict()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Content = "local";
        var disk = s.Read();
        disk["papers"]![0]!["content"] = "external";
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2);
        Require(preview.State != null && preview.Result.ConflictCount == 1 &&
                preview.Conflicts is { Count: 1 }, "conflicting field was not detected");
        var bucket = ConflictAggregator.Group(preview.Conflicts!, preview.ExternalJson).Single();
        Require(bucket is PaperBucket p && p.PaperId == "note-a",
                "same-paper conflicts did not aggregate by stable ID");
        var selected = JsonPath.ApplyPaperChoices(preview.State!,
            new Dictionary<string, PaperConflictChoice> { ["note-a"] = PaperConflictChoice.KeepExternal },
            preview.ExternalJson!);
        var (conflictFile, diffFile) = s.Store.CommitExternalMerge(
            selected, preview.ObservedDiskJson!, 2, preview.Conflicts);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "external",
                "KeepExternal did not commit the external paper");
        Require(conflictFile != null && diffFile != null &&
                File.Exists(Path.Combine(s.DirectoryPath, conflictFile)) &&
                File.Exists(Path.Combine(s.DirectoryPath, diffFile)),
                "conflict source and metadata were not archived");
        Require(File.ReadAllText(Path.Combine(s.DirectoryPath, conflictFile)) ==
                preview.ObservedDiskJson, "conflict archive changed original input");
    }

    private static void MergeRejectChangedSource()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Title = "local";
        var disk = s.Read(); disk["papers"]![0]!["content"] = "external"; s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2);
        Require(preview.State != null, "merge preview did not start");
        disk["papers"]![0]!["content"] = "arrived-later"; s.Write(disk);
        Throws<ExternalDocumentChangedException>(() =>
            s.Store.CommitExternalMerge(preview.State!, preview.ObservedDiskJson!, 2, preview.Conflicts));
        Require(Str(s.Read()["papers"]![0]!["content"]) == "arrived-later",
                "newer external input was overwritten after preview");
    }

    private static void MergeRejectDuringReplace()
    {
        using var s = new Scope();
        var writer = new DurableAtomicFileWriter((stage, path) =>
        {
            if (stage == DurableAtomicWriteStage.BeforeReplace)
                File.WriteAllText(path, "{\"arrived\":true}");
        });
        var special = new StateStore(s.DirectoryPath, writer);
        var local = special.Load();
        local.Papers[0].Title = "local";
        var disk = s.Read(); disk["papers"]![0]!["content"] = "external"; s.Write(disk);
        var preview = special.PreviewExternalMerge(local, 2);
        Require(preview.State != null, "merge preview failed before replace-race test");
        Throws<ExternalDocumentChangedException>(() =>
            special.CommitExternalMerge(preview.State!, preview.ObservedDiskJson!, 2, preview.Conflicts));
        Require(File.ReadAllText(s.Store.FilePath) == "{\"arrived\":true}",
                "guard allowed stale primary replacement during flush");
        Require(!File.Exists(s.Store.FilePath + ".tmp"),
                "stale temporary file was not cleaned after aborted merge");
    }

    private static void LargeConflictDiff()
    {
        var original = string.Join("\n", Enumerable.Range(0, 2000).Select(i => "line-" + i));
        var incoming = original.Replace("line-1000", "line-changed", StringComparison.Ordinal);
        var diff = LineDiff.DiffLines(original, incoming);
        Require(diff.Count(c => c.Kind == LineDiff.ChangeKind.Removed) == 1 &&
                diff.Count(c => c.Kind == LineDiff.ChangeKind.Added) == 1,
                "large-conflict bounded fallback changed diff semantics");
    }
    private sealed class ConcurrentReplaceOperations : IDurableAtomicFileOperations
    {
        public int ReplaceCalls { get; private set; }
        public void FlushToDisk(FileStream stream) => stream.Flush(flushToDisk: true);
        public void Replace(string tempPath, string targetPath)
        {
            ReplaceCalls++;
            if (ReplaceCalls == 1)
            {
                // Another editor writes between the first failed replace and retry.
                File.WriteAllText(targetPath, "{\\"changedDuringRetry\\":true}");
                throw new IOException("Simulated sharing violation after external edit.");
            }
            File.Move(tempPath, targetPath, overwrite: true);
        }
        public void Delay(TimeSpan delay) { }
        public void Delete(string path) => File.Delete(path);
    }

    private static void MergeRejectDuringRetry()
    {
        using var s = new Scope();
        var operations = new ConcurrentReplaceOperations();
        var writer = new DurableAtomicFileWriter(fileOperations: operations);
        var store = new StateStore(s.DirectoryPath, writer);
        var current = store.Load();
        current.Papers[0].Title = "LOCAL";
        var disk = s.Read(); disk["papers"]![0]!["content"] = "external"; s.Write(disk);
        var preview = store.PreviewExternalMerge(current, 2);
        Require(preview.State != null, "retry preview failed");
        Throws<ExternalDocumentChangedException>(() =>
            store.CommitExternalMerge(preview.State!, preview.ObservedDiskJson!, 2, preview.Conflicts));
        Require(operations.ReplaceCalls == 1,
            "writer retried despite an external change detected before replacement");
        Require(File.ReadAllText(s.Store.FilePath) == "{\\"changedDuringRetry\\":true}",
            "merge replaced a newer file on retry");
    }

}
