using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    private static void ConflictResolutionChecks()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("external null and missing values are applied distinctly", ConflictNullAndMissing),
            ("escaped identities and dictionary keys round-trip through conflict paths", ConflictEscapedPaths),
            ("paper choices preserve independent local and external edits", ConflictPaperKeepsIndependentEdits),
            ("todo delete/edit choices affect only the selected entity", ConflictTodoDeletion),
            ("external todo restoration retains incoming neighbor order", ConflictTodoRestoration),
            ("order choices retain independent additions and their neighbors", ConflictOrderKeepsAdditions),
            ("one-sided reordering still composes with content edits", ConflictOneSidedOrder),
            ("paper aggregation retains all conflicts and stable bucket order", ConflictGroupingScale)
        };
        foreach (var check in checks)
        {
            check.Run();
            Console.WriteLine("PASS " + check.Name);
        }
    }

    private static JsonObject ChooseExternal(StateMergeResult merge, JsonObject external) =>
        JsonPath.ApplyChoices(merge.State, merge.Conflicts,
            merge.Conflicts.ToDictionary(c => c.Path, _ => ConflictChoice.KeepExternal, StringComparer.Ordinal), external);

    private static void ConflictNullAndMissing()
    {
        foreach (var missing in new[] { false, true })
        {
            var baseline = J("""{"setting":"original","other":"original"}""");
            var memory = (JsonObject)baseline.DeepClone();
            var external = (JsonObject)baseline.DeepClone();
            memory["setting"] = "local";
            memory["other"] = "independent local";
            if (missing) external.Remove("setting"); else external["setting"] = null;
            var merge = StateJsonMerge.Merge(baseline, memory, external);
            Require(merge.Conflicts.Count == 1 && merge.Conflicts[0].ExternalExists == !missing,
                "missing and null became indistinguishable conflicts");
            var before = merge.State.ToJsonString();
            var source = external.ToJsonString();
            var selected = ChooseExternal(merge, external);
            Require(selected.ContainsKey("setting") == !missing && selected["setting"] == null &&
                Str(selected["other"]) == "independent local", "external clearing was ignored or changed a sibling");
            Require(merge.State.ToJsonString() == before && external.ToJsonString() == source,
                "resolution mutated its input snapshots");
        }
    }

    private static void ConflictEscapedPaths()
    {
        foreach (var id in new[] { "paper.with.dot", "paper]with[brackets", "paper%2E雪", "paper space" })
        {
            var baseline = J("""{"papers":[{"id":"placeholder","type":"note","content":"original"}]}""");
            baseline["papers"]![0]!["id"] = id;
            var memory = (JsonObject)baseline.DeepClone();
            var external = (JsonObject)baseline.DeepClone();
            memory["papers"]![0]!["content"] = "local";
            external["papers"]![0]!["content"] = "external";
            var merge = StateJsonMerge.Merge(baseline, memory, external);
            Require(ConflictAggregator.Group(merge.Conflicts, external).Single() is PaperBucket bucket && bucket.PaperId == id,
                "valid paper identity was split or decoded incorrectly");
            Require(Str(ChooseExternal(merge, external)["papers"]![0]!["content"]) == "external",
                "escaped entity conflict targeted the wrong path");
        }
        foreach (var key in new[] { "plugin.example", "key[id=not-an-entity]", "%2E", "$order" })
        {
            var baseline = new JsonObject { [key] = "original" };
            var memory = new JsonObject { [key] = "local" };
            var external = new JsonObject { [key] = "external" };
            var selected = ChooseExternal(StateJsonMerge.Merge(baseline, memory, external), external);
            Require(selected.Count == 1 && Str(selected[key]) == "external", "dictionary key became path syntax");
        }
    }

    private static void ConflictPaperKeepsIndependentEdits()
    {
        using var s = new Scope();
        var todo = s.Memory.Papers.Single(p => p.Id == "todo");
        todo.Title = "local title";
        todo.Items.Add(new() { Id = "local-new", Text = "independent local", Order = 2 });
        var disk = s.Read();
        disk["papers"]![1]!["title"] = "external title";
        disk["papers"]![1]!["items"]![0]!["text"] = "independent external";
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        Require(preview.State != null && preview.Conflicts is { Count: 1 }, "expected one title conflict");
        var before = s.Store.SerializeState(preview.State!);
        var selected = JsonPath.ApplyPaperChoices(preview.State!,
            new Dictionary<string, PaperConflictChoice> { ["todo"] = PaperConflictChoice.KeepExternal },
            preview.ExternalJson!, preview.Conflicts!);
        var accepted = selected.Papers.Single(p => p.Id == "todo");
        Require(accepted.Title == "external title" && accepted.Items.Any(i => i.Id == "local-new") &&
            accepted.Items.Single(i => i.Id == "t1").Text == "independent external",
            "paper conflict selection replaced independently merged edits");
        Require(s.Store.SerializeState(preview.State!) == before, "paper selection changed its candidate input");
        s.Store.CommitExternalMerge(selected, preview.ObservedDiskJson!, 2, preview.Conflicts);
        Require(s.Read()["papers"]![1]!["items"]!.AsArray().Any(i => Str(i!["id"]) == "local-new"),
            "independent addition did not survive the actual disk commit");
    }

    private static void ConflictTodoDeletion()
    {
        using var s = new Scope();
        var todo = s.Memory.Papers.Single(p => p.Id == "todo");
        todo.Items[0].Text = "conflicting local edit";
        todo.Items[1].Text = "independent local edit";
        var disk = s.Read();
        disk["papers"]![1]!["items"]!.AsArray().RemoveAt(0);
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        Require(preview.State != null && preview.Conflicts is { Count: 1 }, "expected todo delete/edit conflict");
        var selected = JsonPath.ApplyPaperChoices(preview.State!,
            new Dictionary<string, PaperConflictChoice> { ["todo"] = PaperConflictChoice.KeepExternal },
            preview.ExternalJson!, preview.Conflicts!);
        var items = selected.Papers.Single(p => p.Id == "todo").Items;
        Require(items.Count == 1 && items[0].Id == "t2" && items[0].Text == "independent local edit",
            "todo deletion was ignored or replaced a nonconflicting sibling");
    }

    private static void ConflictTodoRestoration()
    {
        var baseline = J("""{"papers":[{"id":"p","items":[{"id":"a","text":"original"},{"id":"b","text":"original"}]}]}""");
        var memory = (JsonObject)baseline.DeepClone();
        var external = (JsonObject)baseline.DeepClone();
        memory["papers"]![0]!["items"]!.AsArray().RemoveAt(0);
        memory["papers"]![0]!["items"]![0]!["text"] = "local sibling";
        external["papers"]![0]!["items"]![0]!["text"] = "external restored";
        var selected = ChooseExternal(StateJsonMerge.Merge(baseline, memory, external), external);
        var items = selected["papers"]![0]!["items"]!.AsArray();
        Require(items.Select(i => Str(i!["id"])).SequenceEqual(new[] { "a", "b" }) &&
            Str(items[0]!["text"]) == "external restored" && Str(items[1]!["text"]) == "local sibling",
            "restoration appended out of order or replaced a sibling");
    }

    private static void ConflictOrderKeepsAdditions()
    {
        var baseline = J("""{"papers":[{"id":"a"},{"id":"b"},{"id":"c"}]}""");
        var memory = J("""{"papers":[{"id":"b"},{"id":"a"},{"id":"local-new"},{"id":"c"}]}""");
        var external = J("""{"papers":[{"id":"c"},{"id":"a"},{"id":"b"}]}""");
        var merge = StateJsonMerge.Merge(baseline, memory, external);
        Require(merge.Conflicts.Count == 1 && merge.Conflicts[0].Path == "papers.$order", "expected order conflict");
        var selected = ChooseExternal(merge, external);
        Require(selected["papers"]!.AsArray().Select(i => Str(i!["id"]))
            .SequenceEqual(new[] { "c", "a", "local-new", "b" }), "order choice moved an independent addition away from its neighbor");
    }

    private static void ConflictOneSidedOrder()
    {
        var baseline = J("""{"papers":[{"id":"a","content":"original"},{"id":"b","content":"original"}]}""");
        foreach (var externalReorders in new[] { false, true })
        {
            var edited = (JsonObject)baseline.DeepClone();
            edited["papers"]![0]!["content"] = "independent edit";
            var reordered = J("""{"papers":[{"id":"b","content":"original"},{"id":"a","content":"original"}]}""");
            var merge = externalReorders ? StateJsonMerge.Merge(baseline, edited, reordered) : StateJsonMerge.Merge(baseline, reordered, edited);
            Require(merge.Conflicts.Count == 0 && Str(merge.State["papers"]![0]!["id"]) == "b" &&
                Str(merge.State["papers"]![1]!["content"]) == "independent edit", "one-sided order stopped composing with content edits");
        }
    }

    private static void ConflictGroupingScale()
    {
        var conflicts = Enumerable.Range(0, 3000).Select(i => new StateMergeConflict(
            JsonPath.EntityPath("papers", "p.with.dot") + ".items[id=t" + i + "].text",
            true, JsonValue.Create("original"), true, JsonValue.Create("local"), true, JsonValue.Create("external"))).ToList();
        conflicts.Add(new("zoom", true, JsonValue.Create(1), true, JsonValue.Create(2), true, JsonValue.Create(3)));
        var grouped = ConflictAggregator.Group(conflicts, null);
        Require(grouped.Count == 2 && grouped[0] is SingleLeafBucket && grouped[1] is PaperBucket paper &&
            paper.Conflicts.Count == 3000 && ReferenceEquals(paper.Conflicts[2999], conflicts[2999]),
            "large paper grouping lost conflicts or changed bucket order");
    }
}
