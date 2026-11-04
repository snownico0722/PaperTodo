using System.Diagnostics;
using System.Text.Json;
using PaperTodo;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Empty workspace", () => Assert(Layout([]).Cards.Count == 0)),
            ("Initial packing uses both axes", () => {
                var result = Layout(Nodes(20)); NoOverlaps(result);
                Assert(result.Cards.Values.Select(r => r.X).Distinct().Count() > 1);
                Assert(result.Cards.Values.Select(r => r.Y).Distinct().Count() > 1);
            }),
            ("Variable sizes, 200 papers", () => {
                var result = Layout(Nodes(200)); NoOverlaps(result); Finite(result);
            }),
            ("Directed graph and cycle routing", () => {
                var nodes = Nodes(8); var edges = Enumerable.Range(0, 8).Select(i => new WorkbenchLayoutEdge(nodes[i].Id, nodes[(i + 1) % 8].Id)).ToArray();
                var result = Layout(nodes, edges); NoOverlaps(result); Finite(result); Assert(result.Routes.Count == 8);
                RoutesAvoidOtherCards(result);
            }),
            ("Deterministic full layout independent of input order", () => {
                var nodes = Nodes(25); var edges = new[] { new WorkbenchLayoutEdge("p000", "p001"), new WorkbenchLayoutEdge("p001", "p002") };
                var first = Layout(nodes, edges, true); var second = Layout(nodes.Reverse().ToArray(), edges.Reverse().ToArray(), true);
                EqualGeometry(first, second);
            }),
            ("Incremental insertion preserves every clear incumbent", () => {
                var nodes = Nodes(30); var first = Layout(nodes);
                var next = nodes.Select(n => n with { Previous = first.Cards[n.Id] }).Append(new("new", 480, 600)).ToArray();
                var second = Layout(next); NoOverlaps(second);
                foreach (var pair in first.Cards) Assert(second.Cards[pair.Key] == pair.Value, "Incumbent moved: " + pair.Key);
            }),
            ("Incremental deletion preserves positions", () => {
                var nodes = Nodes(16); var first = Layout(nodes);
                var second = Layout(nodes.Skip(1).Select(n => n with { Previous = first.Cards[n.Id] }).ToArray());
                foreach (var pair in second.Cards) Assert(pair.Value == first.Cards[pair.Key]);
            }),
            ("Hard pin stays fixed during full arrangement", () => {
                var pin = new WorkbenchRect(-1500, 300, 320, 360);
                var nodes = Nodes(20); nodes[0] = nodes[0] with { Width = pin.Width, Height = pin.Height, Previous = pin, Pinned = true };
                var result = Layout(nodes, arrange: true); Assert(result.Cards[nodes[0].Id] == pin); NoOverlaps(result);
            }),
            ("Overlapping hard pins reported rather than moved", () => {
                WorkbenchLayoutNode[] nodes = [new("a", 320, 360, new(0, 0, 320, 360), true), new("b", 320, 360, new(20, 20, 320, 360), true), new("c", 320, 360)];
                var result = Layout(nodes, [new("a", "b")]);
                Assert(result.LockedOverlaps.Count == 1); Assert(result.Cards["a"].X == 0 && result.Cards["b"].X == 20);
                Assert(!result.Cards["c"].Overlaps(result.Cards["a"])); Assert(!result.Cards["c"].Overlaps(result.Cards["b"])); Finite(result);
            }),
            ("Just-dragged paper has precedence over movable neighbors", () => {
                WorkbenchLayoutNode[] nodes = [new("a", 320, 360, new(0, 0, 320, 360)), new("b", 320, 360, new(0, 0, 320, 360))];
                var result = WorkbenchLayoutEngine.Calculate(nodes, [], preferredId: "b");
                Assert(result.Cards["b"].X == 0 && result.Cards["b"].Y == 0); NoOverlaps(result);
            }),
            ("Pinned obstacle overrides drag preference", () => {
                WorkbenchLayoutNode[] nodes = [new("a", 320, 360, new(0, 0, 320, 360), true), new("b", 320, 360, new(0, 0, 320, 360))];
                var result = WorkbenchLayoutEngine.Calculate(nodes, [], preferredId: "b"); Assert(result.Cards["a"].X == 0); NoOverlaps(result);
            }),
            ("Related newcomer gets an adjacent insertion", () => {
                WorkbenchLayoutNode[] nodes = [new("a", 320, 360, new(10000, 5000, 320, 360)), new("b", 240, 280)];
                var result = Layout(nodes, [new("a", "b")]);
                Assert(Math.Abs(result.Cards["b"].Center.X - result.Cards["a"].Center.X) < 400); NoOverlaps(result);
            }),
            ("Shared app affinity creates a dashed neighborhood", () => {
                WorkbenchLayoutNode[] nodes = [new("a", 320, 360, Affinity: "app:123"), new("b", 240, 280, Affinity: "app:123"), new("c", 320, 360)];
                var result = Layout(nodes); Assert(result.Sections.Any(s => s.Key == "app:123" && s.Members.Count == 2));
            }),
            ("Dangling and duplicate edges do not enter routing", () => {
                var result = Layout(Nodes(3), [new("p000", "p001"), new("p000", "p001"), new("missing", "p001"), new("p000", "p000")]);
                Assert(result.Routes.Count == 1);
            }),
            ("Repeated incremental layout is idempotent", () => {
                var nodes = Nodes(24); var first = Layout(nodes);
                var second = Layout(nodes.Select(n => n with { Previous = first.Cards[n.Id] }).ToArray()); EqualGeometry(first, second);
            }),
            ("Cancellation is observed", () => {
                var canceled = new CancellationToken(true);
                try { WorkbenchLayoutEngine.Calculate(Nodes(10), [], cancellationToken: canceled); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { }
            }),
            ("Invalid coordinates rejected", () => {
                try { Layout([new("bad", double.NaN, 100)]); throw new Exception("Invalid size accepted"); }
                catch (ArgumentException) { }
            }),
            ("Metadata normalization and deep-copy isolation", () => {
                var state = new WorkbenchState { Cards = new() { ["a"] = new() { X = 5, Width = -10 }, ["deleted"] = new(), ["b"] = new() { X = double.NaN } } };
                state.Normalize(["a", "b", "c"]); Assert(state.Cards.Count == 1 && state.Cards["a"].Width == 200);
                var copy = state.Copy(); copy.Cards["a"].X = 9;
                Assert(state.Cards["a"].X == 5);
                var json = JsonSerializer.Serialize(state); var restored = JsonSerializer.Deserialize<WorkbenchState>(json)!;
                Assert(restored.Cards["a"].X == 5);
            }),
            ("Dense pinned wall has terminating collision resolution", () => {
                var nodes = Enumerable.Range(0, 100).Select(i => new WorkbenchLayoutNode($"pin{i:000}", 240, 260, new((i % 10) * 272, (i / 10) * 292, 240, 260), true))
                    .Append(new("new", 700, 600, new(1000, 1000, 700, 600))).ToArray();
                var result = Layout(nodes); NoOverlaps(result); Finite(result);
            })
        };
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            var watch = Stopwatch.StartNew();
            try { run(); Console.WriteLine($"PASS {name} ({watch.ElapsedMilliseconds} ms)"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} workbench layout checks passed.");
        return failed == 0 ? 0 : 1;
    }
    private static WorkbenchLayoutNode[] Nodes(int count) => Enumerable.Range(0, count).Select(i =>
        new WorkbenchLayoutNode($"p{i:000}", 240 + i % 7 * 50, 240 + i % 11 * 35)).ToArray();
    private static WorkbenchLayoutResult Layout(WorkbenchLayoutNode[] nodes, WorkbenchLayoutEdge[]? edges = null, bool arrange = false) =>
        WorkbenchLayoutEngine.Calculate(nodes, edges ?? [], arrange);
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void NoOverlaps(WorkbenchLayoutResult result)
    {
        var cards = result.Cards.ToArray();
        for (var i = 0; i < cards.Length; i++) for (var j = i + 1; j < cards.Length; j++)
            Assert(!cards[i].Value.Overlaps(cards[j].Value), $"Overlap: {cards[i].Key}/{cards[j].Key}");
    }
    private static void Finite(WorkbenchLayoutResult result)
    {
        foreach (var r in result.Cards.Values) Assert(double.IsFinite(r.X) && double.IsFinite(r.Y) && r.Width > 0 && r.Height > 0);
        foreach (var point in result.Routes.SelectMany(r => r.Points)) Assert(double.IsFinite(point.X) && double.IsFinite(point.Y));
    }
    private static void EqualGeometry(WorkbenchLayoutResult first, WorkbenchLayoutResult second)
    {
        Assert(first.Cards.Count == second.Cards.Count);
        foreach (var pair in first.Cards) Assert(pair.Value == second.Cards[pair.Key], "Non-deterministic geometry: " + pair.Key);
    }
    private static void RoutesAvoidOtherCards(WorkbenchLayoutResult result)
    {
        foreach (var route in result.Routes)
        foreach (var card in result.Cards.Where(p => p.Key != route.From && p.Key != route.To))
            foreach (var point in route.Points)
                Assert(!card.Value.Contains(point), $"Route {route.From}->{route.To} crosses {card.Key}");
    }
}
