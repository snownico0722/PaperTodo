using Microsoft.Msagl.Core;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using Microsoft.Msagl.Routing;
using MsPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace PaperTodo;

internal sealed record WorkbenchLayoutNode(
    string Id, double Width, double Height, WorkbenchRect? Previous = null,
    bool Pinned = false, bool Preferred = false, string Affinity = "", string Kind = "note");
internal sealed record WorkbenchLayoutEdge(string From, string To);
internal sealed record WorkbenchSection(string Key, IReadOnlyList<string> Members, WorkbenchRect Bounds);
internal sealed record WorkbenchRoute(string From, string To, IReadOnlyList<WorkbenchPoint> Points);
internal sealed record WorkbenchLayoutResult(
    IReadOnlyDictionary<string, WorkbenchRect> Cards,
    IReadOnlyList<WorkbenchSection> Sections,
    IReadOnlyList<WorkbenchRoute> Routes,
    IReadOnlyList<(string First, string Second)> LockedOverlaps)
{
    internal WorkbenchRect Bounds => WorkbenchRect.Union(Sections.Select(s => s.Bounds)
        .Concat(Cards.Values)).Inflate(56);
}

/// <summary>
/// No WPF objects, model writes or shared MSAGL graphs. Full arrangement uses layered graph
/// layout; ordinary edits keep existing positions and solve only collisions/new placements.
/// Hard pins are never relaxed. Conflicting pins are reported, not silently moved.
/// </summary>
internal static class WorkbenchLayoutEngine
{
    internal const double CardGap = 32;
    internal const double SectionPadding = 32;
    private const double SectionGap = 96;

    internal static WorkbenchLayoutResult Calculate(
        IReadOnlyList<WorkbenchLayoutNode> input,
        IReadOnlyList<WorkbenchLayoutEdge> connections,
        bool arrangeAll = false, string? preferredId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = input.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();
        if (nodes.Any(n => string.IsNullOrWhiteSpace(n.Id) || !double.IsFinite(n.Width) ||
            !double.IsFinite(n.Height) || n.Width <= 0 || n.Height <= 0 ||
            n.Previous is { } p && (!double.IsFinite(p.X) || !double.IsFinite(p.Y))))
            throw new ArgumentException("Layout nodes require finite bounds and stable identities.", nameof(input));
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = connections.Where(e => e.From != e.To && byId.ContainsKey(e.From) && byId.ContainsKey(e.To))
            .Distinct().OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToArray();
        if (nodes.Length == 0) return new(new Dictionary<string, WorkbenchRect>(), [], [], []);
        var groups = Group(nodes, edges);
        var ideal = arrangeAll || nodes.Any(n => n.Previous is null)
            ? InitialArrangement(groups, edges, cancellationToken)
            : nodes.ToDictionary(n => n.Id, n => n.Previous!.Value, StringComparer.Ordinal);
        var neighborIds = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            neighborIds[edge.From].Add(edge.To);
            neighborIds[edge.To].Add(edge.From);
        }
        var groupByNode = groups.SelectMany(g => g.Nodes.Select(n => (n.Id, Group: g)))
            .ToDictionary(p => p.Id, p => p.Group, StringComparer.Ordinal);
        var placed = new Dictionary<string, WorkbenchRect>(StringComparer.Ordinal);
        var index = new OccupiedSpace();
        var lockedOverlaps = new List<(string, string)>();

        foreach (var node in nodes.Where(n => n.Pinned && n.Previous is not null))
        {
            var bounds = new WorkbenchRect(node.Previous!.Value.X, node.Previous.Value.Y, node.Width, node.Height);
            foreach (var collision in index.Intersections(bounds, 0)) lockedOverlaps.Add((collision.Id, node.Id));
            placed.Add(node.Id, bounds); index.Add(node.Id, bounds);
        }

        // Place the just-dragged paper before soft preferences, but after explicit pins.
        // Incumbents precede newcomers: adding a paper never shuffles an already clear desk.
        var movable = nodes.Where(n => !placed.ContainsKey(n.Id))
            .OrderBy(n => n.Id == preferredId ? 0 : n.Previous is not null && n.Preferred ? 1 : n.Previous is not null ? 2 : 3)
            .ThenBy(n => ideal[n.Id].Y).ThenBy(n => ideal[n.Id].X).ThenBy(n => n.Id, StringComparer.Ordinal);
        foreach (var node in movable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ideal[node.Id];
            if (!arrangeAll && node.Previous is { } previous)
                target = new(previous.X, previous.Y, node.Width, node.Height);
            else if (arrangeAll && node.Preferred && node.Previous is { } preferred)
                target = target.At(new(preferred.X * .75 + target.X * .25, preferred.Y * .75 + target.Y * .25));
            else if (!arrangeAll && nodes.Any(n => n.Previous is not null))
            {
                var neighbors = neighborIds[node.Id].Where(placed.ContainsKey).Select(id => placed[id]).ToArray();
                if (neighbors.Length > 0)
                    target = target.At(new(neighbors.Average(r => r.Center.X) - node.Width / 2,
                        neighbors.Max(r => r.Bottom) + CardGap));
                else
                {
                    var siblings = groupByNode[node.Id].Nodes.Where(n => placed.ContainsKey(n.Id)).Select(n => placed[n.Id]).ToArray();
                    if (siblings.Length > 0)
                    {
                        var bounds = WorkbenchRect.Union(siblings);
                        target = target.At(new(bounds.X, bounds.Bottom + CardGap));
                    }
                }
            }
            var result = FindSpace(target, index, cancellationToken);
            placed.Add(node.Id, result); index.Add(node.Id, result);
        }
        var sections = groups.Select(g => new WorkbenchSection(g.Key, g.Nodes.Select(n => n.Id).ToArray(),
            WorkbenchRect.Union(g.Nodes.Select(n => placed[n.Id])).Inflate(SectionPadding))).ToArray();
        var routes = Route(placed, edges, lockedOverlaps.Count > 0, cancellationToken);
        return new(placed, sections, routes, lockedOverlaps);
    }

    private sealed record GroupInfo(string Key, WorkbenchLayoutNode[] Nodes);

    private static GroupInfo[] Group(WorkbenchLayoutNode[] nodes, WorkbenchLayoutEdge[] edges)
    {
        var parent = nodes.ToDictionary(n => n.Id, n => n.Id, StringComparer.Ordinal);
        string Root(string id)
        {
            while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; }
            return id;
        }
        void Join(string a, string b)
        {
            a = Root(a); b = Root(b);
            if (StringComparer.Ordinal.Compare(a, b) > 0) (a, b) = (b, a);
            parent[b] = a;
        }
        foreach (var e in edges) Join(e.From, e.To);
        foreach (var affinity in nodes.Where(n => n.Affinity.Length > 0).GroupBy(n => n.Affinity, StringComparer.Ordinal))
        {
            var first = affinity.First();
            foreach (var node in affinity.Skip(1)) Join(first.Id, node.Id);
        }
        var components = nodes.GroupBy(n => Root(n.Id)).Select(g => g.ToArray()).ToArray();
        var groups = new List<GroupInfo>();
        foreach (var component in components.Where(g => g.Length > 1 || g[0].Affinity.Length > 0))
        {
            var sameAffinity = component[0].Affinity.Length > 0 && component.All(n => n.Affinity == component[0].Affinity);
            groups.Add(new(sameAffinity ? component[0].Affinity : "related:" + component[0].Id, component));
        }
        // Unrelated papers get loose visual neighborhoods, not a new user-maintained taxonomy.
        foreach (var loose in components.Where(g => g.Length == 1 && g[0].Affinity.Length == 0)
                     .Select(g => g[0]).GroupBy(n => n.Kind, StringComparer.Ordinal))
            groups.Add(new("loose:" + loose.Key, loose.ToArray()));
        return groups.OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, WorkbenchRect> InitialArrangement(
        GroupInfo[] groups, WorkbenchLayoutEdge[] edges, CancellationToken token)
    {
        var local = new Dictionary<string, WorkbenchRect>(StringComparer.Ordinal);
        var groupSizes = new List<(string Id, double Width, double Height)>();
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var ids = group.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var groupEdges = edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToArray();
            Dictionary<string, WorkbenchRect> positions;
            if (groupEdges.Length == 0)
                positions = Pack(group.Nodes.Select(n => (n.Id, n.Width, n.Height)), CardGap);
            else
            {
                var graph = new GeometryGraph();
                var map = group.Nodes.ToDictionary(n => n.Id, n => new Node(
                    CurveFactory.CreateRectangle(n.Width, n.Height, new MsPoint(0, 0))), StringComparer.Ordinal);
                foreach (var node in group.Nodes) graph.Nodes.Add(map[node.Id]);
                foreach (var edge in groupEdges) graph.Edges.Add(new Edge(map[edge.From], map[edge.To]));
                var settings = new SugiyamaLayoutSettings
                {
                    NodeSeparation = CardGap * 2,
                    LayerSeparation = 96,
                    RandomSeedForOrdering = 1,
                    RepetitionCoefficientForOrdering = 2
                };
                var cancel = new CancelToken();
                using var registration = token.Register(() => cancel.Canceled = true);
                LayoutHelpers.CalculateLayout(graph, settings, cancel);
                token.ThrowIfCancellationRequested();
                // MSAGL is Cartesian (Y up); the workbench is a screen coordinate system.
                positions = group.Nodes.ToDictionary(n => n.Id, n => new WorkbenchRect(
                    map[n.Id].Center.X - n.Width / 2, -map[n.Id].Center.Y - n.Height / 2, n.Width, n.Height), StringComparer.Ordinal);
            }
            var bounds = WorkbenchRect.Union(positions.Values);
            foreach (var pair in positions) local[pair.Key] = pair.Value.Offset(new(-bounds.X + SectionPadding, -bounds.Y + SectionPadding));
            groupSizes.Add((group.Key, bounds.Width + SectionPadding * 2, bounds.Height + SectionPadding * 2));
        }
        var packed = Pack(groupSizes, SectionGap);
        foreach (var group in groups)
            foreach (var node in group.Nodes) local[node.Id] = local[node.Id].Offset(packed[group.Key].Location);
        return local;
    }

    /// <summary>Variable-size, deterministic best-fit shelves; no fixed-size grid assumptions.</summary>
    private static Dictionary<string, WorkbenchRect> Pack(
        IEnumerable<(string Id, double Width, double Height)> items, double gap)
    {
        var ordered = items.OrderByDescending(i => i.Height).ThenByDescending(i => i.Width)
            .ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, WorkbenchRect>(StringComparer.Ordinal);
        if (ordered.Length == 0) return result;
        var targetWidth = Math.Max(ordered.Max(i => i.Width),
            Math.Sqrt(ordered.Sum(i => (i.Width + gap) * (i.Height + gap)) * 1.6));
        var shelves = new List<(double Y, double Height, double Used)>();
        foreach (var item in ordered)
        {
            var best = -1; var remaining = double.PositiveInfinity;
            for (var s = 0; s < shelves.Count; s++)
            {
                var shelf = shelves[s];
                var free = targetWidth - shelf.Used - item.Width;
                if (item.Height <= shelf.Height && free >= 0 && free < remaining) { best = s; remaining = free; }
            }
            if (best < 0)
            {
                var y = shelves.Count == 0 ? 0 : shelves[^1].Y + shelves[^1].Height + gap;
                best = shelves.Count; shelves.Add((y, item.Height, 0));
            }
            var selected = shelves[best];
            result[item.Id] = new(selected.Used, selected.Y, item.Width, item.Height);
            shelves[best] = (selected.Y, selected.Height, selected.Used + item.Width + gap);
        }
        return result;
    }

    private static WorkbenchRect FindSpace(WorkbenchRect target, OccupiedSpace occupied, CancellationToken token)
    {
        var queue = new PriorityQueue<WorkbenchRect, (double Distance, long Order)>();
        var visited = new HashSet<(long, long)>();
        long order = 0;
        void Add(WorkbenchRect r)
        {
            if (visited.Add(((long)Math.Round(r.X * 100), (long)Math.Round(r.Y * 100))))
                queue.Enqueue(r, ((r.Location - target.Location).LengthSquared, order++));
        }
        Add(target);
        // Boundary candidates find nearby holes without scanning the whole infinite plane.
        for (var attempt = 0; attempt < 1024 && queue.TryDequeue(out var candidate, out _); attempt++)
        {
            token.ThrowIfCancellationRequested();
            var collisions = occupied.Intersections(candidate, CardGap);
            if (collisions.Count == 0) return candidate;
            foreach (var (_, other) in collisions)
            {
                Add(candidate with { X = other.X - candidate.Width - CardGap });
                Add(candidate with { X = other.Right + CardGap });
                Add(candidate with { Y = other.Y - candidate.Height - CardGap });
                Add(candidate with { Y = other.Bottom + CardGap });
            }
        }
        // The unbounded desk always has free space outside the occupied envelope.
        // This guarantees termination even with a pathological wall of pinned cards.
        return target with { X = occupied.Right + CardGap };
    }

    private sealed class OccupiedSpace
    {
        private const double CellSize = 512;
        private readonly Dictionary<(int, int), List<(string Id, WorkbenchRect Rect)>> _cells = new();
        internal double Right { get; private set; } = double.NegativeInfinity;
        private static IEnumerable<(int, int)> Cells(WorkbenchRect r)
        {
            for (var y = (int)Math.Floor(r.Y / CellSize); y <= (int)Math.Floor(r.Bottom / CellSize); y++)
            for (var x = (int)Math.Floor(r.X / CellSize); x <= (int)Math.Floor(r.Right / CellSize); x++) yield return (x, y);
        }
        internal void Add(string id, WorkbenchRect rect)
        {
            Right = Math.Max(Right, rect.Right);
            foreach (var cell in Cells(rect))
            {
                if (!_cells.TryGetValue(cell, out var items)) _cells[cell] = items = new();
                items.Add((id, rect));
            }
        }
        internal List<(string Id, WorkbenchRect Rect)> Intersections(WorkbenchRect rect, double gap)
        {
            var found = new Dictionary<string, WorkbenchRect>(StringComparer.Ordinal);
            foreach (var cell in Cells(rect.Inflate(gap)))
                if (_cells.TryGetValue(cell, out var items))
                    foreach (var item in items)
                        if (rect.Overlaps(item.Rect, gap)) found.TryAdd(item.Id, item.Rect);
            return found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList();
        }
    }

    private static IReadOnlyList<WorkbenchRoute> Route(IReadOnlyDictionary<string, WorkbenchRect> cards,
        WorkbenchLayoutEdge[] edges, bool conflictingPins, CancellationToken token)
    {
        if (edges.Length == 0) return [];
        var graph = new GeometryGraph();
        var map = cards.ToDictionary(p => p.Key, p => new Node(CurveFactory.CreateRectangle(
            p.Value.Width, p.Value.Height, new MsPoint(p.Value.Center.X, -p.Value.Center.Y))), StringComparer.Ordinal);
        foreach (var pair in cards.OrderBy(p => p.Key, StringComparer.Ordinal)) graph.Nodes.Add(map[pair.Key]);
        foreach (var edge in edges) graph.Edges.Add(new Edge(map[edge.From], map[edge.To]));
        if (!conflictingPins)
        {
            var cancel = new CancelToken();
            using var registration = token.Register(() => cancel.Canceled = true);
            new SplineRouter(graph, 8, 12, Math.PI / 6).Run(cancel);
        }
        var routes = new List<WorkbenchRoute>(edges.Length);
        for (var i = 0; i < edges.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var edge = edges[i];
            var curve = graph.Edges[i].Curve;
            if (curve is null)
            {
                // Overlapping hard pins have no obstacle-free route. Keep the relationship
                // visible and report the pin conflict rather than inventing valid geometry.
                routes.Add(new(edge.From, edge.To, [cards[edge.From].Center, cards[edge.To].Center]));
                continue;
            }
            var points = new WorkbenchPoint[49];
            for (var p = 0; p < points.Length; p++)
            {
                var point = curve[curve.ParStart + (curve.ParEnd - curve.ParStart) * p / (points.Length - 1)];
                points[p] = new(point.X, -point.Y);
            }
            routes.Add(new(edge.From, edge.To, points));
        }
        return routes;
    }
}
