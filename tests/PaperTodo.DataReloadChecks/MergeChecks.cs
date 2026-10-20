using System.IO;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    private static void MergeIndependentEdits()
    {
        ConflictResolutionChecks();
        using var s = new Scope();
        s.Memory.Papers[0].Title = "local";
        var disk = s.Read();
        disk["papers"]![0]!["content"] = "external";
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        Require(preview.State != null && preview.Result.Outcome == "pending" &&
                preview.Conflicts is { Count: 0 } &&
                preview.State.Papers[0].Title == "local" &&
                preview.State.Papers[0].Content == "external",
                "independent edits were not retained");
        var archived = s.Store.CommitExternalMerge(ResolvePreview(preview), preview.ObservedDiskJson!, 2);
        Require(archived.ConflictFile == null && archived.ConflictDiffFile == null,
                "clean merge generated conflict files");
        Require(Str(s.Read()["papers"]![0]!["title"]) == "local" &&
                Str(s.Read()["papers"]![0]!["content"]) == "external",
                "accepted merge was not written to primary");
        Require(!File.ReadAllText(s.Store.FilePath).Contains("$paperTodoRevision", StringComparison.Ordinal),
                "opt-in merge added obsolete persistent revision tracking");
        Require(s.Store.PreviewExternalMerge(preview.State, 3).Result.Outcome == "unchanged",
                "identical current and disk state should require no merge");
    }

    private static void MergeOriginalBaseline()
    {
        foreach (var asynchronous in new[] { false, true })
        foreach (var sameField in new[] { false, true })
        {
            using var s = new Scope();
            var disk = s.Read(); // The external editor reads before PaperTodo's next save.
            if (sameField) s.Memory.Papers[0].Content = "saved local body";
            else s.Memory.Papers[0].Title = "SAVED";
            var saved = s.Store.SerializeState(s.Memory);
            if (asynchronous) s.Store.SaveJsonAsync(saved, 2).GetAwaiter().GetResult();
            else s.Store.SaveJsonSync(saved, 2);
            disk["papers"]![0]!["content"] = "external from original";
            s.Write(disk);
            var before = File.ReadAllText(s.Store.FilePath);
            var preview = s.Store.PreviewExternalMerge(s.Memory, 3, s.BaselineJson);
            Require(preview.State != null && preview.Result.Outcome == "pending" &&
                preview.Result.ConflictCount == (sameField ? 1 : 0),
                "a later save displaced the editor's original baseline");
            Require(preview.State.Papers[0].Content == (sameField ? "saved local body" : "external from original") &&
                (sameField || preview.State.Papers[0].Title == "SAVED"),
                "merge reverted saved local work or lost external work");
            Require(File.ReadAllText(s.Store.FilePath) == before, "preview wrote the file before resolution");
        }
    }

    private static void MergeMissingBaseline()
    {
        using var s = new Scope();
        var stale = NewState();
        stale.Papers[0].Content = "obsolete queued save";
        Require(s.Store.PreviewExternalMerge(s.Memory, 2).Result.Outcome == "unchanged",
            "identical snapshots should not need a baseline");
        s.Store.SaveJsonSync(s.Store.SerializeState(stale), 1);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "original",
            "unchanged merge did not fence an older queued snapshot");
        var disk = s.Read();
        s.Memory.Papers[0].Title = "SAVED";
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 3);
        disk["papers"]![0]!["content"] = "external based on old file";
        s.Write(disk);
        var before = File.ReadAllText(s.Store.FilePath);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 4);
        Require(preview.State != null && preview.Conflicts is { Count: 1 } &&
            preview.Conflicts[0].Reason == "unknown_baseline" &&
            ConflictAggregator.Group(preview.Conflicts, preview.ExternalJson).Single() is UnknownBaselineBucket,
            "missing baseline was silently inferred from a save");
        Require(preview.State.Papers[0].Title == "SAVED" &&
            preview.State.Papers[0].Content == "original" && File.ReadAllText(s.Store.FilePath) == before,
            "unknown baseline applied external state before the user's choice");
    }

    private static void MergeInvalidBaseline()
    {
        using var s = new Scope();
        var disk = File.ReadAllText(s.Store.FilePath);
        var memory = s.Store.SerializeState(s.Memory);
        foreach (var invalid in new[] { "", "{broken", "null", "[]", "{\"papers\":{}}" })
        {
            var preview = s.Store.PreviewExternalMerge(s.Memory, 2, invalid);
            Require(preview.State == null && preview.Result.Outcome == "invalid" &&
                File.ReadAllText(s.Store.FilePath) == disk && s.Store.SerializeState(s.Memory) == memory,
                "invalid baseline was ignored or changed state");
        }
    }

    private static void MergeUneditedExternal()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Title = "LOCAL";
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        Require(preview.State?.Papers[0].Title == "LOCAL" && preview.Conflicts is { Count: 0 },
            "unchanged external file incorrectly suppressed local edits");
    }

    private static void MergePaperConflict()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Content = "local";
        var disk = s.Read();
        disk["papers"]![0]!["content"] = "external";
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        Require(preview.State != null && preview.Result.ConflictCount == 1 &&
                preview.Conflicts is { Count: 1 }, "conflicting field was not detected");
        var bucket = ConflictAggregator.Group(preview.Conflicts!, preview.ExternalJson).Single();
        Require(bucket is PaperBucket p && p.PaperId == "note-a",
                "same-paper conflicts did not aggregate by stable ID");
        var selected = JsonPath.ApplyPaperChoices(preview.State!,
            new Dictionary<string, PaperConflictChoice> { ["note-a"] = PaperConflictChoice.KeepExternal },
            preview.ExternalJson!, preview.Conflicts!);
        var (conflictFile, diffFile) = s.Store.CommitExternalMerge(
            ResolvePreview(preview, ConflictChoice.KeepExternal), preview.ObservedDiskJson!, 2);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "external",
                "KeepExternal did not commit the external paper");
        Require(conflictFile != null && diffFile != null &&
                File.Exists(Path.Combine(s.DirectoryPath, conflictFile)) &&
                File.Exists(Path.Combine(s.DirectoryPath, diffFile)),
                "conflict source and metadata were not archived");
        Require(File.ReadAllText(Path.Combine(s.DirectoryPath, conflictFile)) ==
                preview.ObservedDiskJson, "conflict archive changed original input");
    }

    private static void MergePaperDeletionConflicts()
    {
        foreach (var paperId in new[] { "note-a", "todo" })
        foreach (var externalDeletes in new[] { true, false })
        foreach (var choice in new[] { PaperConflictChoice.KeepMemory, PaperConflictChoice.KeepExternal })
        {
            using var s = new Scope();
            var local = s.Memory.Papers.Single(p => p.Id == paperId);
            var survivor = s.Memory.Papers.Single(p => p.Id != paperId);
            var disk = s.Read();
            var incoming = disk["papers"]!.AsArray().Single(p => Str(p!["id"]) == paperId)!;
            if (externalDeletes)
            {
                if (local.Type == PaperTypes.Note) local.Content = "local edit";
                else local.Items[0].Text = "local edit";
                disk["papers"]!.AsArray().Remove(incoming);
            }
            else
            {
                s.Memory.Papers.Remove(local);
                if (local.Type == PaperTypes.Note) incoming["content"] = "external edit";
                else incoming["items"]![0]!["text"] = "external edit";
            }
            s.Write(disk);
            var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
            Require(preview.State != null && preview.Conflicts is { Count: 1 },
                "delete/edit must produce one whole-paper conflict");
            Require(ConflictAggregator.Group(preview.Conflicts!, preview.ExternalJson).Single()
                is PaperBucket bucket && bucket.PaperId == paperId,
                "delete/edit conflict did not reach the paper dialog");
            var candidateBefore = s.Store.SerializeState(preview.State!);
            var externalBefore = preview.ExternalJson!.ToJsonString();
            var selected = JsonPath.ApplyPaperChoices(preview.State!,
                new Dictionary<string, PaperConflictChoice> { [paperId] = choice }, preview.ExternalJson, preview.Conflicts!);
            var shouldExist = choice == PaperConflictChoice.KeepExternal ? !externalDeletes : externalDeletes;
            Require(selected.Papers.Any(p => p.Id == paperId) == shouldExist,
                $"{paperId}: externalDeletes={externalDeletes}, choice={choice} ignored existence");
            if (shouldExist)
            {
                var paper = selected.Papers.Single(p => p.Id == paperId);
                var content = paper.Type == PaperTypes.Note ? paper.Content : paper.Items[0].Text;
                Require(content == (externalDeletes ? "local edit" : "external edit"),
                    "delete/edit choice lost the selected paper contents");
                Require(selected.Papers.Select(p => p.Id).SequenceEqual(new[] { "note-a", "todo" }),
                    "restored paper lost its position beside surviving papers");
            }
            Require(s.Store.SerializeState(preview.State!) == candidateBefore &&
                preview.ExternalJson.ToJsonString() == externalBefore, "selection mutated its source snapshots");
            s.Store.CommitExternalMerge(ResolvePreview(preview, choice == PaperConflictChoice.KeepExternal
                ? ConflictChoice.KeepExternal : ConflictChoice.KeepMemory), preview.ObservedDiskJson!, 2);
            Require(s.Read()["papers"]!.AsArray().Any(p => Str(p!["id"]) == paperId) == shouldExist,
                "persisted paper existence disagrees with the user's choice");
            StateReloadModels.Apply(s.Memory, selected);
            Require(s.Memory.Papers.Any(p => p.Id == paperId) == shouldExist &&
                ReferenceEquals(survivor, s.Memory.Papers.Single(p => p.Id == survivor.Id)),
                "reconciliation changed the choice or replaced an unrelated paper");
        }
    }

    private static void MergeRejectChangedSource()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Title = "local";
        var disk = s.Read(); disk["papers"]![0]!["content"] = "external"; s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        Require(preview.State != null, "merge preview did not start");
        disk["papers"]![0]!["content"] = "arrived-later"; s.Write(disk);
        Throws<ExternalDocumentChangedException>(() =>
            s.Store.CommitExternalMerge(ResolvePreview(preview), preview.ObservedDiskJson!, 2));
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
        var preview = special.PreviewExternalMerge(local, 2, s.BaselineJson);
        Require(preview.State != null, "merge preview failed before replace-race test");
        Throws<ExternalDocumentChangedException>(() =>
            special.CommitExternalMerge(ResolvePreview(preview), preview.ObservedDiskJson!, 2));
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
                File.WriteAllText(targetPath, "{\"changedDuringRetry\":true}");
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
        var preview = store.PreviewExternalMerge(current, 2, s.BaselineJson);
        Require(preview.State != null, "retry preview failed");
        Throws<ExternalDocumentChangedException>(() =>
            store.CommitExternalMerge(ResolvePreview(preview), preview.ObservedDiskJson!, 2));
        Require(operations.ReplaceCalls == 1,
            "writer retried despite an external change detected before replacement");
        Require(File.ReadAllText(s.Store.FilePath) == "{\"changedDuringRetry\":true}",
            "merge replaced a newer file on retry");
    }

}
