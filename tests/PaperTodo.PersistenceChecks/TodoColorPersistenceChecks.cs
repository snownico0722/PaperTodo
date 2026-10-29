using System.IO;
using PaperTodo;

internal static class TodoColorPersistenceChecks
{
    internal static void Run()
    {
        using var scope = new TempDirectory();
        var store = new StateStore(scope.Path, DurableAtomicFileWriter.Shared);
        File.WriteAllText(store.FilePath, """{"papers":[{"id":"legacy","type":"todo","title":"keep","items":[{"text":"original"}]}],"colorScheme":"forest"}""");
        var legacy = store.Load();
        Require(legacy.TodoColorSlots?.Count == 4 && legacy.Papers[0].ColorSlotId == null && legacy.ColorScheme == "forest", "legacy settings changed");
        Require(legacy.Papers[0].Items[0].Text == "original", "legacy todo lost");
        var state = new AppState();
        var shared = state.TodoColorSlots![2];
        shared.Name = "科研";
        for (var i = 0; i < 7; i++) state.Papers.Add(new PaperData
        {
            Id = "paper-" + i, Title = "collection " + i,
            ColorSlotId = i < 4 ? shared.Id : i == 4 ? null : state.TodoColorSlots[i - 5].Id,
            Items = [new PaperItem { Text = "task " + i }]
        });
        var replacement = shared.Copy();
        replacement.Preset = null; replacement.PrimaryColor = "#2468AC";
        var slots = state.TodoColorSlots.Select(slot => slot.Id == shared.Id ? replacement : slot.Copy()).ToList();
        var publishCount = 0;
        var edit = TodoColorSlots.Change(state, slots, new Dictionary<PaperData, string?>(), () => publishCount++);
        PaperSettingsService.CommitChange(edit, () => { store.SaveJsonSync(store.SerializeState(state), 1); return true; }, action => action());
        var restored = store.Load();
        Require(publishCount == 1 && restored.Papers.Count == 7, "edit publication or roundtrip failed");
        Require(restored.Papers.Take(4).All(paper => TodoColorSlots.Resolve(restored, paper)?.PrimaryColor == "#2468AC"), "shared users not updated");
        Require(TodoColorSlots.Resolve(restored, restored.Papers[5])?.Preset == ColorSchemes.Warm && restored.Papers[4].ColorSlotId == null, "unrelated paper color changed");
        Require(restored.Papers.Select(paper => paper.Items[0].Text).SequenceEqual(Enumerable.Range(0, 7).Select(i => "task " + i)), "content changed");

        var assignments = restored.Papers.Where(paper => paper.ColorSlotId == shared.Id).ToDictionary(paper => paper, _ => (string?)null);
        var remaining = restored.TodoColorSlots!.Where(slot => slot.Id != shared.Id).ToList();
        var deletion = TodoColorSlots.Change(restored, remaining, assignments, () => publishCount++);
        try
        {
            PaperSettingsService.CommitChange(deletion, () => false, action => action());
            throw new InvalidOperationException("failed commit accepted");
        }
        catch (PaperSettingsException ex) when (ex.Code == "save_failed") { }
        Require(assignments.Keys.All(paper => paper.ColorSlotId == shared.Id) && restored.TodoColorSlots!.Count == 4 && publishCount == 1,
            "failed deletion did not restore assignments and slots");
        Require(store.Load().Papers.Take(4).All(paper => paper.ColorSlotId == shared.Id), "failed save changed disk");
        PaperSettingsService.CommitChange(deletion, () => { store.SaveJsonSync(store.SerializeState(restored), 2); return true; }, action => action());
        Require(store.Load().Papers.Take(4).All(paper => paper.ColorSlotId == null), "deleted references not cleared");

        restored.TodoColorSlots = [];
        store.SaveJsonSync(store.SerializeState(restored), 3);
        Require(store.Load().TodoColorSlots?.Count == 0, "intentionally empty slots reseeded");
        restored.TodoColorSlots = [new TodoColorSlot { Id = "bad", PrimaryColor = "#GG0000" }];
        restored.Papers[0].ColorSlotId = "bad"; restored.Papers[1].ColorSlotId = "missing";
        store.SaveJsonSync(store.SerializeState(restored), 4);
        var invalid = store.Load();
        Require(invalid.Papers.Count == 7 && invalid.TodoColorSlots?.Count == 1, "invalid slot destroyed core data");
        Require(TodoColorSlots.Resolve(invalid, invalid.Papers[0]) == null && TodoColorSlots.Resolve(invalid, invalid.Papers[1]) == null, "invalid reference did not inherit");
        invalid.TodoColorSlots = TodoColorSlots.Defaults(); invalid.Papers[0].Type = PaperTypes.Note; invalid.Papers[0].ColorSlotId = invalid.TodoColorSlots[0].Id;
        Require(TodoColorSlots.Resolve(invalid, invalid.Papers[0]) == null, "note gained local palette");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
