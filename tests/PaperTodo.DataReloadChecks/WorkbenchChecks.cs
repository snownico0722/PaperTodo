using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using PaperTodo;

internal static partial class Program
{
    private static void WorkbenchChecks()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("transfer append preserves the target document and fences old writers", WorkbenchTransferAppend),
            ("transfer append rejects duplicates, full and missing targets without writing", WorkbenchTransferRejects),
            ("preview read never recovers, normalizes into the store or writes", WorkbenchPreviewIsReadOnly),
            ("positions and links normalize on load and repair on save", WorkbenchStateNormalization),
            ("edge resistance: 90% grows, 95% crosses only toward a neighbour", WorkbenchEdgeResistance),
            ("full arrange is deterministic, overlap-free and groups relations", WorkbenchArrange),
            ("light placement joins relations and kind groups without merging regions", WorkbenchPlacement),
            ("drop resolution moves only the dropped card off its neighbours", WorkbenchDrop),
            ("dashed regions follow card proximity", WorkbenchRegions)
        };
        foreach (var check in checks)
        {
            check.Run();
            Console.WriteLine("PASS workbench: " + check.Name);
        }
    }

    private static JsonObject TransferNode(string id, string text = "moved") =>
        StateStore.SerializePaperForWorkbenchTransfer(new PaperData
        {
            Id = id,
            Type = PaperTypes.Todo,
            Items = [new() { Id = id + "-1", Text = text }],
            WorkbenchX = 400,
            WorkbenchY = 300
        });

    private static void WorkbenchTransferAppend()
    {
        using var scope = new Scope();
        var target = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        target.SaveJsonSync(target.SerializeState(NewState()), 1);
        var source = J(File.ReadAllText(target.FilePath));
        source["futureExtension"] = J("{\"keep\":true}");
        File.WriteAllText(target.FilePath, source.ToJsonString());

        target.AppendWorkbenchTransferredPaper(TransferNode("moved"), 5);
        var saved = J(File.ReadAllText(target.FilePath));
        var papers = saved["papers"]!.AsArray();
        Require(papers.Count == 3 && papers[2]!["id"]!.GetValue<string>() == "moved",
            "transferred paper was not appended last");
        Require(JsonNode.DeepEquals(papers[0], source["papers"]![0]) &&
                JsonNode.DeepEquals(papers[1], source["papers"]![1]) &&
                JsonNode.DeepEquals(saved["futureExtension"], source["futureExtension"]),
            "transfer rewrote other papers or unknown properties");

        var loaded = target.Load();
        var moved = loaded.Papers.Single(p => p.Id == "moved");
        Require(moved.WorkbenchX == 400 && moved.WorkbenchY == 300 && moved.Items.Single().Text == "moved",
            "transferred paper did not load with its canvas position");

        // An older queued save of that workspace must not undo the transfer.
        target.SaveJsonSync(target.SerializeState(NewState()), 4);
        Require(J(File.ReadAllText(target.FilePath))["papers"]!.AsArray().Count == 3,
            "an older save replaced the transfer");
        Throws<WorkbenchTransferException>(() => target.AppendWorkbenchTransferredPaper(TransferNode("late"), 3));
    }

    private static void WorkbenchTransferRejects()
    {
        using var scope = new Scope();
        var missing = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 3);
        RequireTransferCode(() => missing.AppendWorkbenchTransferredPaper(TransferNode("x"), 2), "missing");
        Require(!File.Exists(missing.FilePath), "a missing target workspace was created");

        var bytes = File.ReadAllBytes(scope.Store.FilePath);
        RequireTransferCode(() => scope.Store.AppendWorkbenchTransferredPaper(TransferNode("todo"), 3), "duplicate");
        Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(bytes), "a rejected duplicate wrote the file");

        var full = NewState();
        full.Papers = Enumerable.Range(0, StateStore.WorkbenchMaxPapersPerDocument)
            .Select(i => new PaperData { Id = "p" + i })
            .ToList();
        scope.Store.SaveJsonSync(scope.Store.SerializeState(full), 4);
        bytes = File.ReadAllBytes(scope.Store.FilePath);
        RequireTransferCode(() => scope.Store.AppendWorkbenchTransferredPaper(TransferNode("extra"), 5), "paper_limit");
        Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(bytes), "a full target was written");

        var failing = new StateStore(scope.DirectoryPath, new DurableAtomicFileWriter((stage, _) =>
        {
            if (stage == DurableAtomicWriteStage.BeforeReplace) throw new IOException("injected transfer failure");
        }));
        scope.Store.SaveJsonSync(scope.Store.SerializeState(NewState()), 6);
        bytes = File.ReadAllBytes(scope.Store.FilePath);
        Throws<IOException>(() => failing.AppendWorkbenchTransferredPaper(TransferNode("io"), 7));
        Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(bytes), "a failed transfer replaced the target");
    }

    private static void RequireTransferCode(Action action, string code)
    {
        try { action(); }
        catch (WorkbenchTransferException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected workbench transfer rejection " + code);
    }

    private static void WorkbenchPreviewIsReadOnly()
    {
        using var scope = new Scope();
        var secondary = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        Throws<FileNotFoundException>(() => secondary.ReadDocumentForWorkbenchPreview());

        var state = NewState();
        state.Papers[0].WorkbenchX = 10;
        state.Papers[0].WorkbenchY = 20;
        secondary.SaveJsonSync(secondary.SerializeState(state), 1);
        secondary.TryRefreshBackupFromPrimary();
        var bytes = File.ReadAllBytes(secondary.FilePath);
        var preview = secondary.ReadDocumentForWorkbenchPreview();
        Require(preview.Papers.Count == 2 && preview.Papers[0].WorkbenchX == 10, "preview did not read the document");
        Require(File.ReadAllBytes(secondary.FilePath).SequenceEqual(bytes), "preview wrote the document");

        File.WriteAllText(secondary.FilePath, "{ broken");
        Throws<System.Text.Json.JsonException>(() => secondary.ReadDocumentForWorkbenchPreview());
        Require(File.ReadAllText(secondary.FilePath) == "{ broken", "preview replaced a corrupt primary from backup");
    }

    private static void WorkbenchStateNormalization()
    {
        using var scope = new Scope();
        var state = NewState();
        state.Papers[0].WorkbenchX = double.NaN;
        state.Papers[0].WorkbenchY = 5;
        state.Papers[0].WorkbenchLinks = ["todo", "todo", "note-a", "ghost", ""];
        state.Papers[1].WorkbenchLinks = ["note-a"];
        state.Papers[1].WorkbenchX = 30;
        state.Papers[1].WorkbenchY = 40;
        scope.Store.SaveJsonSync(scope.Store.SerializeState(state), 2);
        var saved = J(File.ReadAllText(scope.Store.FilePath));
        Require(saved["papers"]![0]!["workbenchX"] == null, "non-finite canvas position was serialized");

        var loaded = scope.Store.Load();
        var note = loaded.Papers.Single(p => p.Id == "note-a");
        var todo = loaded.Papers.Single(p => p.Id == "todo");
        Require(note.WorkbenchX == null && note.WorkbenchY == null, "a half position survived load");
        Require(note.WorkbenchLinks is ["todo"], "dangling, self, blank or duplicate links survived load");
        Require(todo.WorkbenchLinks == null, "the mirrored copy of a pair survived load");
        Require(todo.WorkbenchX == 30 && todo.WorkbenchY == 40, "a valid canvas position was lost");
        Require(WorkbenchPaperState.Pairs(loaded.Papers).Count == 1, "pair enumeration is not undirected");

        Require(!WorkbenchPaperState.TryLink(todo, note), "an existing pair was linked again from the other side");
        Require(WorkbenchPaperState.Unlink(todo, note) && !WorkbenchPaperState.AreLinked(note, todo),
            "unlink did not remove the pair");
        Require(WorkbenchPaperState.TryLink(todo, note) &&
                WorkbenchPaperState.RemoveLinksTo(loaded.Papers, "note-a").Single() == todo &&
                todo.WorkbenchLinks == null,
            "deleting a paper did not remove links to it");
    }

    private static void WorkbenchEdgeResistance()
    {
        var canvas = new Size(2000, 1000);
        var none = WorkbenchLayout.EvaluateEdge(new Point(1000, 500), canvas, true, true);
        Require(!none.Extends && none.Cross == 0, "the middle of the canvas reacted");
        var grow = WorkbenchLayout.EvaluateEdge(new Point(1810, 500), canvas, true, true);
        Require(grow.ExtendRight && !grow.ExtendDown && grow.Cross == 0, "90% did not extend");
        var cross = WorkbenchLayout.EvaluateEdge(new Point(1910, 950), canvas, true, true);
        Require(cross.Cross == 1 && !cross.Extends, "95% toward a neighbour must stop growing and cross");
        var lastCanvas = WorkbenchLayout.EvaluateEdge(new Point(1990, 500), canvas, true, false);
        Require(lastCanvas.ExtendRight && lastCanvas.Cross == 0, "an edge without a neighbour must keep growing");
        var left = WorkbenchLayout.EvaluateEdge(new Point(60, 500), canvas, true, true);
        Require(left.Cross == -1, "the left edge did not cross toward the previous workspace");
        var firstCanvas = WorkbenchLayout.EvaluateEdge(new Point(60, 500), canvas, false, true);
        Require(firstCanvas.Cross == 0 && !firstCanvas.Extends, "the first canvas offered a left crossing");
        var down = WorkbenchLayout.EvaluateEdge(new Point(1000, 920), canvas, false, false);
        Require(down.ExtendDown && !down.ExtendRight, "the bottom edge did not extend");
        Require(WorkbenchLayout.AutoPanSpeed < WorkbenchLayout.ExtendRatio * WorkbenchLayout.ExtendSpeed,
            "holding at the window edge would drift into crossing");
    }

    private static WorkbenchLayoutCard Card(string id, WorkbenchCardKind kind, Point? origin = null) =>
        new(id, kind, kind.ToString(), origin);

    private static void RequireNoOverlap(IReadOnlyDictionary<string, Point> origins, string message)
    {
        var rects = origins.Values.Select(WorkbenchLayout.CardRect).ToArray();
        for (var i = 0; i < rects.Length; i++)
            for (var j = i + 1; j < rects.Length; j++)
            {
                var a = rects[i];
                var b = rects[j];
                if (a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
                    throw new InvalidOperationException(message);
            }
    }

    private static void WorkbenchArrange()
    {
        var cards = new List<WorkbenchLayoutCard>();
        for (var i = 0; i < 9; i++) cards.Add(Card("t" + i, WorkbenchCardKind.Todo));
        for (var i = 0; i < 5; i++) cards.Add(Card("n" + i, WorkbenchCardKind.Note));
        cards.Add(Card("hub", WorkbenchCardKind.Note));
        for (var i = 0; i < 6; i++) cards.Add(Card("leaf" + i, WorkbenchCardKind.Todo));
        cards.Add(new WorkbenchLayoutCard("plug", WorkbenchCardKind.Plugin, "plugin:x", null));
        var edges = Enumerable.Range(0, 6).Select(i => ("hub", "leaf" + i)).ToList();
        edges.Add(("leaf0", "leaf1"));

        var first = WorkbenchLayout.Arrange(cards, edges, 2400);
        var second = WorkbenchLayout.Arrange(cards, edges, 2400);
        Require(first.Count == cards.Count, "arrange dropped cards");
        Require(first.All(pair => second[pair.Key] == pair.Value), "arrange is not deterministic");
        RequireNoOverlap(first, "arrange produced overlapping cards");
        Require(first.Values.All(p => p.X >= WorkbenchLayout.CanvasPadding && p.Y >= WorkbenchLayout.CanvasPadding),
            "arrange ignored the canvas padding");

        // The hub is the tree root: leaves sit one level to its right or left, around its row.
        var hub = first["hub"];
        var level = WorkbenchLayout.CardWidth + WorkbenchLayout.TreeLevelGap;
        Require(Enumerable.Range(0, 6).All(i => Math.Abs(Math.Abs(first["leaf" + i].X - hub.X) - level) < 0.5),
            "linked papers were not laid out as one mind-map level around the root");
        Require(Enumerable.Range(0, 6).Count(i => first["leaf" + i].X > hub.X) == 3,
            "branches were not balanced between both sides");

        // Groups are separated far enough to read as different dashed regions.
        var regions = WorkbenchLayout.Regions(first.Select(pair => (pair.Key, WorkbenchLayout.CardRect(pair.Value))).ToArray());
        var todoRegion = regions.Single(region => region.CardIds.Contains("t0"));
        Require(Enumerable.Range(0, 9).All(i => todoRegion.CardIds.Contains("t" + i)) &&
                !todoRegion.CardIds.Contains("n0") && !todoRegion.CardIds.Contains("hub"),
            "kind groups or relation trees merged into one region");
        Require(regions.Single(region => region.CardIds.Contains("hub")).CardIds.Count == 7,
            "the relation tree is not one region");
    }

    private static void WorkbenchPlacement()
    {
        var placed = new Dictionary<string, Rect>
        {
            ["a"] = WorkbenchLayout.CardRect(new Point(100, 100)),
            ["b"] = WorkbenchLayout.CardRect(new Point(100 + WorkbenchLayout.CardWidth + WorkbenchLayout.CardGap, 100)),
            ["far"] = WorkbenchLayout.CardRect(new Point(1400, 100))
        };
        var beside = WorkbenchLayout.PlaceNew(placed, ["far"], [], 2000);
        Require(beside.X > 1400 + WorkbenchLayout.CardWidth && Math.Abs(beside.Y - 100) < 0.5,
            "a linked paper was not placed beside the paper it links to");

        var grouped = WorkbenchLayout.PlaceNew(placed, [], ["a", "b"], 2000);
        Require(grouped.Y > 100 + WorkbenchLayout.CardHeight - 0.5 && Math.Abs(grouped.X - 100) < 0.5,
            "a kind group was not continued on its next row");
        Require(WorkbenchLayout.IsFree(WorkbenchLayout.CardRect(grouped), placed.Values.ToArray(), 0),
            "placement overlapped an existing card");

        var alone = WorkbenchLayout.PlaceNew(placed, [], [], 2000);
        var rect = WorkbenchLayout.CardRect(alone);
        Require(WorkbenchLayout.IsFree(rect, placed.Values.ToArray(), WorkbenchLayout.RegionJoinDistance),
            "an unrelated paper joined an existing region");
    }

    private static void WorkbenchDrop()
    {
        var others = new[] { WorkbenchLayout.CardRect(new Point(300, 300)) };
        var canvas = new Size(2000, 1200);
        var free = WorkbenchLayout.ResolveDrop(new Point(900, 300), others, canvas);
        Require(free == new Point(900, 300), "a free drop was moved");
        var nudged = WorkbenchLayout.ResolveDrop(new Point(320, 320), others, canvas);
        Require(WorkbenchLayout.IsFree(WorkbenchLayout.CardRect(nudged), others, 0),
            "an overlapping drop was not resolved");
        Require((nudged - new Point(320, 320)).Length < WorkbenchLayout.CardWidth,
            "the drop moved further than needed");
        var clamped = WorkbenchLayout.ResolveDrop(new Point(-50, 5000), Array.Empty<Rect>(), canvas);
        Require(clamped.X == 0 && clamped.Y == canvas.Height - WorkbenchLayout.CardHeight,
            "a drop outside the frame was not clamped inside it");
    }

    private static void WorkbenchRegions()
    {
        var near = WorkbenchLayout.CardWidth + WorkbenchLayout.RegionJoinDistance - 10;
        var far = WorkbenchLayout.CardWidth + WorkbenchLayout.RegionJoinDistance + 10;
        var regions = WorkbenchLayout.Regions(new (string, Rect)[]
        {
            ("a", WorkbenchLayout.CardRect(new Point(0, 0))),
            ("b", WorkbenchLayout.CardRect(new Point(near, 0))),
            ("c", WorkbenchLayout.CardRect(new Point(near + far, 0))),
            ("d", WorkbenchLayout.CardRect(new Point(5000, 0)))
        });
        Require(regions.Count == 1 && regions[0].CardIds.SequenceEqual(new[] { "a", "b" }),
            "regions did not follow card proximity, or a lone card got a frame");
        Require(regions[0].Bounds.Left == -WorkbenchLayout.RegionPadding,
            "the dashed frame does not surround its cards");
    }
}
