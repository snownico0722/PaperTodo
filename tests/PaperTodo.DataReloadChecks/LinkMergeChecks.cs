using System.IO;
using PaperTodo;

internal static partial class Program
{
    private static void MergeDeletedPaperRetainsChosenLinks()
    {
        // A Todo link is unchanged on both sides. Deleting its target on ONE side
        // must not turn load-time cleanup into a fabricated user unlink.
        foreach (var externalDeletes in new[] { true, false })
        foreach (var keepTarget in new[] { true, false })
        {
            using var scope = new Scope();
            scope.Memory.Papers.Single(p => p.Id == "todo").Items[0].LinkPaper("note-a");
            scope.Store.SaveJsonSync(scope.Store.SerializeState(scope.Memory), 2);
            var baseline = File.ReadAllText(scope.Store.FilePath);
            var disk = scope.Read();
            var memoryNote = scope.Memory.Papers.Single(p => p.Id == "note-a");
            var diskNote = disk["papers"]!.AsArray().Single(p =>
                p!["id"]!.GetValue<string>() == "note-a")!;
            if (externalDeletes)
            {
                memoryNote.Content = "locally edited before external deletion";
                disk["papers"]!.AsArray().Remove(diskNote);
            }
            else
            {
                scope.Memory.Papers.Remove(memoryNote);
                diskNote["content"] = "externally edited before internal deletion";
            }
            scope.Write(disk);
            var preview = scope.Store.PreviewExternalMerge(scope.Memory, 3, baseline);
            Require(preview.State != null && preview.Conflicts is { Count: 1 },
                "delete/edit should conflict only on the target paper");
            var choice = keepTarget == externalDeletes
                ? ConflictChoice.KeepMemory : ConflictChoice.KeepExternal;
            scope.Store.CommitExternalMerge(ResolvePreview(preview, choice),
                preview.ObservedDiskJson!, 3);
            var persisted = scope.Store.Load();
            Require(persisted.Papers.Any(p => p.Id == "note-a") == keepTarget,
                "final paper existence ignored the selected delete/edit choice");
            var linkedId = persisted.Papers.Single(p => p.Id == "todo").Items[0].LinkedPaperId;
            Require(linkedId == (keepTarget ? "note-a" : null),
                "merge cleanup lost a surviving target link or retained a dangling link");
        }

        // A true external unlink (with its target still present) is a real edit and
        // must still win. Do not blindly restore every old association.
        foreach (var replaceWithPath in new[] { false, true })
        {
            using var scope = new Scope();
            scope.Memory.Papers.Single(p => p.Id == "todo").Items[0].LinkPaper("note-a");
            scope.Store.SaveJsonSync(scope.Store.SerializeState(scope.Memory), 2);
            var baseline = File.ReadAllText(scope.Store.FilePath);
            var disk = scope.Read();
            var todo = disk["papers"]!.AsArray().Single(p =>
                p!["id"]!.GetValue<string>() == "todo")!;
            var item = todo["items"]!.AsArray()[0]!.AsObject();
            item.Remove("linkedNoteId");
            if (replaceWithPath)
            {
                item["linkedPath"] = @"C:\Notes\example.txt";
                item["linkedPathIsDirectory"] = false;
            }
            scope.Write(disk);
            var preview = scope.Store.PreviewExternalMerge(scope.Memory, 3, baseline);
            Require(preview.State != null && preview.Conflicts is { Count: 0 },
                "explicit unlink created a false conflict");
            scope.Store.CommitExternalMerge(ResolvePreview(preview),
                preview.ObservedDiskJson!, 3);
            var persisted = scope.Store.Load();
            var result = persisted.Papers.Single(p => p.Id == "todo").Items[0];
            Require(result.LinkedPaperId == null &&
                    result.LinkedPath == (replaceWithPath ? @"C:\Notes\example.txt" : null),
                "explicit external unlink or path link was not preserved");
        }
        Console.WriteLine("PASS delete/edit final choices retain only valid Todo links and real unlink wins");
    }
}
