using System.IO;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    private static void WorkspaceMaterialsStorageChecks()
    {
        WorkspaceMaterialsMigration();
        WorkspaceMaterialsMerge();
        WorkspaceMaterialsImages();
        WorkspaceMaterialsInvalidImport();
        Console.WriteLine("PASS workspace materials: migration, authoritative content, identity merge, image recovery and invalid imports");
    }

    private static void WorkspaceMaterialsMigration()
    {
        using var s = new Scope();
        s.Memory.Workbench = new()
        {
            Cards = new() { ["note-a"] = new() { X = 91, Y = -72, Width = 310, Height = 400, Pinned = true } }
        };
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2);
        var legacy = File.ReadAllText(s.Store.FilePath);
        var overview = StateStore.ReadWorkbenchOverview(s.Store.FilePath)!;
        var loaded = s.Store.Load();
        var owner = WorkspaceMaterials.FindOwner(loaded)!;
        Require(overview.Workbench == null && loaded.Workbench == null &&
            owner.Id == WorkspaceMaterials.FindOwner(overview)!.Id && owner.Title == WorkspaceMaterials.DefaultTitle,
            "legacy workbench did not migrate to the same materials owner in load and overview");
        Require(owner.Type == PaperTypes.Note && owner.WorkspaceMaterials!.Items.Count == 0 && owner.Content == "" &&
            owner.WorkspaceMaterials.Layout.Cards["note-a"].X == 91 &&
            owner.WorkspaceMaterials.Layout.Cards["note-a"].Pinned &&
            !owner.WorkspaceMaterials.Layout.Cards.ContainsKey(owner.Id),
            "migration duplicated paper content, lost placement or projected its own owner");
        Require(File.ReadAllText(s.Store.FilePath) == legacy, "read-only migration wrote the workspace document");
        var saved = J(s.Store.SerializeState(loaded));
        Require(!saved.ContainsKey("workbench") && loaded.Papers.Single(p => p.Id == "note-a").Content == "original",
            "migration retained a second layout writer or changed an existing note");

        // An editor can still hold a pre-migration baseline when the live canvas adds a material.
        AddMaterial(owner.WorkspaceMaterials, "material:local", "local item");
        var disk = J(legacy); disk["papers"]![0]!["content"] = "external note"; s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(loaded, 3, legacy);
        Require(preview.State != null && preview.Conflicts is { Count: 0 } &&
            WorkspaceMaterials.FindOwner(preview.State)!.WorkspaceMaterials!.Items.ContainsKey("material:local") &&
            preview.State.Papers.Single(p => p.Id == "note-a").Content == "external note",
            "legacy baseline migration created a false owner conflict or lost material content");
    }

    private static void WorkspaceMaterialsMerge()
    {
        using var s = new Scope();
        var owner = WorkspaceMaterials.CreateOwner(s.Memory);
        var document = owner.WorkspaceMaterials!;
        AddMaterial(document, "material:a", "original A");
        AddMaterial(document, "material:b", "original B");
        AddMaterial(document, "material:c", "original C");
        document.Layout.Cards["material:a"] = new() { X = 33, Width = 180, Height = 96 };
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2);
        var baseline = File.ReadAllText(s.Store.FilePath);
        var memory = s.Store.Load();
        var local = WorkspaceMaterials.FindOwner(memory)!.WorkspaceMaterials!;
        local.Items["material:a"].Content = "local A";
        local.Layout.Connections.Add(new() { Id = "local-link", From = "material:a", To = "material:b" });
        var disk = J(baseline);
        var incoming = disk["papers"]!.AsArray().Single(p => Str(p!["id"]) == owner.Id)!["workspaceMaterials"]!;
        incoming["items"]!["material:b"]!["content"] = "external B";
        incoming["layout"]!["connections"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "external-link", ["from"] = "material:b", ["to"] = "material:c", ["label"] = "", ["isBranch"] = true
        });
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(memory, 3, baseline);
        var merged = preview.State == null ? null : WorkspaceMaterials.FindOwner(preview.State)!.WorkspaceMaterials!;
        Require(merged != null && preview.Conflicts is { Count: 0 } &&
            merged.Items["material:a"].Content == "local A" && merged.Items["material:b"].Content == "external B" &&
            merged.Layout.Connections.Select(c => c.Id).ToHashSet().SetEquals(["local-link", "external-link"]) &&
            merged.Layout.Cards["material:a"].Width == 180 && merged.Layout.Cards["material:a"].Height == 96,
            "independent item/link edits did not merge by stable identity or compact geometry was expanded");
        incoming["items"]!["material:a"]!["content"] = "external A"; s.Write(disk);
        var conflict = s.Store.PreviewExternalMerge(memory, 3, baseline);
        Require(conflict.Conflicts is { Count: 1 } &&
            WorkspaceMaterials.FindOwner(conflict.State!)!.WorkspaceMaterials!.Items["material:a"].Content == "local A",
            "editing one material on both sides failed to preserve a resolvable conflict");
        var resolved = WorkspaceMaterials.FindOwner(ResolvePreview(conflict, ConflictChoice.KeepExternal).State)!.WorkspaceMaterials!;
        Require(resolved.Items["material:a"].Content == "external A" && resolved.Items["material:b"].Content == "external B" &&
            resolved.Layout.Connections.Any(c => c.Id == "local-link"),
            "choosing external material content discarded independently merged connections");

        // Ordinary persistence must not silently prune unnormalized editing state.
        local.Layout.Groups["pending"] = new() { Members = ["temporarily-missing"] };
        local.Layout.Connections.Add(new() { Id = "pending", From = "material:a", To = "temporarily-missing" });
        local.Layout.Cards["material:a"].X = double.NaN;
        var ordinary = J(s.Store.SerializeState(memory));
        var persisted = ordinary["papers"]!.AsArray().Single(p => Str(p!["id"]) == owner.Id)!["workspaceMaterials"]!;
        Require(persisted["layout"]!["groups"]!["pending"] != null &&
            persisted["layout"]!["connections"]!.AsArray().Any(c => Str(c!["id"]) == "pending") &&
            local.Layout.Cards["material:a"].X == 0 && WorkspaceMaterials.FindOwner(memory)!.Content == "",
            "ordinary save pruned editing state or introduced a writable Markdown mirror");
    }

    private static void WorkspaceMaterialsImages()
    {
        using var s = new Scope();
        var document = WorkspaceMaterials.CreateOwner(s.Memory).WorkspaceMaterials!;
        AddMaterial(document, "material:image", "![saved](i:101)");
        AddMaterial(document, "material:reference", "Source description");
        document.Items["material:reference"].Source = "![source](i:404)";
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2);
        Require(s.Store.TryRefreshBackupFromPrimary(), "material backup setup failed");
        document.Items["material:image"].Content = "![live](i:202)";
        var archive = StateStore.CopyForReload(s.Memory);
        WorkspaceMaterials.FindOwner(archive)!.WorkspaceMaterials!.Items["material:image"].Content = "![recovery](i:303)";
        var archivePath = Path.Combine(s.DirectoryPath, "dataconflict-materials.json");
        File.WriteAllText(archivePath, s.Store.SerializeState(archive));
        Require(s.Store.TryCollectProtectedImageIds(s.Memory, out var ids) && ids.IsSupersetOf(["101", "202", "303", "404"]),
            "image GC missed live, saved or archived material content");
        using var missingImages = new NoteImageStore(Path.Combine(s.DirectoryPath, "missing-material-images.lmdb"));
        missingImages.Load();
        missingImages.ProtectPersistedImageReferences(s.Memory, new HashSet<string>(), hasPersistedDocument: true);
        Require(missingImages.IsWriteDisabled, "a missing database with material images was treated as a fresh workspace");
        var invalidArchive = J(File.ReadAllText(archivePath));
        invalidArchive["papers"]!.AsArray().Single(p => p!["workspaceMaterials"] != null)!["workspaceMaterials"]!["items"]!["material:image"] = null;
        File.WriteAllText(archivePath, invalidArchive.ToJsonString());
        // #415 excludes damaged historical files from recovery. Image retirement
        // still protects live, primary, backup and readable archive references.
        Require(s.Store.TryCollectProtectedImageIds(s.Memory, out var protectedIds) &&
                protectedIds.IsSupersetOf(["101", "202", "404"]) && !protectedIds.Contains("303"),
            "malformed historical material entry blocked scanning valid snapshots or protected an unreadable reference");
    }

    private static void WorkspaceMaterialsInvalidImport()
    {
        using var s = new Scope();
        WorkspaceMaterials.CreateOwner(s.Memory);
        var valid = s.Store.SerializeState(s.Memory);
        foreach (var invalid in new[]
        {
            "{\"items\":null}",
            "{\"items\":{\"material:a\":null}}",
            "{\"items\":{\"material:a\":{\"id\":\"material:b\"}}}",
            "{\"layout\":{\"connections\":[{\"id\":\"duplicate\"},{\"id\":\"duplicate\"}]}}"
        })
        {
            var disk = J(valid);
            disk["papers"]!.AsArray().Single(p => p!["workspaceMaterials"] != null)!["workspaceMaterials"] = J(invalid);
            s.Write(disk);
            Require(s.Store.ReloadExternalState(2).Result.Outcome == "invalid", "invalid materials document was accepted: " + invalid);
        }
    }

    private static void AddMaterial(WorkspaceMaterialsDocument document, string id, string content) =>
        document.Items[id] = new() { Id = id, Kind = WorkspaceMaterialKinds.Text, Content = content };
}
