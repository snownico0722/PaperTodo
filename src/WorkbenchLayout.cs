using System.Windows;

namespace PaperTodo;

internal enum WorkbenchCardKind
{
    Todo,
    Note,
    Plugin,
    Tethered
}

/// <summary>One card as seen by the layout engine. <see cref="GroupKey"/> decides which
/// singleton group a card falls into when it has no relation to other cards.</summary>
internal readonly record struct WorkbenchLayoutCard(
    string Id,
    WorkbenchCardKind Kind,
    string GroupKey,
    Point? Origin);

/// <summary>A dashed partition derived from where cards actually are. It is never stored.</summary>
internal sealed record WorkbenchRegion(Rect Bounds, IReadOnlyList<string> CardIds);

internal readonly record struct WorkbenchEdgeState(bool ExtendRight, bool ExtendDown, int Cross)
{
    public static WorkbenchEdgeState None => default;
    public bool Extends => ExtendRight || ExtendDown;
}

/// <summary>
/// Pure workbench geometry: canvas sizing, the 90% / 95% edge resistance, the dashed regions,
/// and the layout engine. The engine has three strengths, from strongest to lightest:
/// <list type="number">
/// <item><see cref="Arrange"/> rebuilds a whole canvas on request: linked papers form
/// mind-map trees, the rest are grouped by kind, and groups are shelf-packed.</item>
/// <item><see cref="PlaceNew"/> gives an unplaced paper a slot beside related papers without
/// moving anything else.</item>
/// <item><see cref="ResolveDrop"/> only nudges the card the user just dropped off its
/// neighbours.</item>
/// </list>
/// Positions the user chose are only rewritten by an explicit <see cref="Arrange"/>.
/// </summary>
internal static class WorkbenchLayout
{
    public const double CardWidth = 248;
    public const double CardHeight = 208;
    public const double CardGap = 28;
    public const double TreeLevelGap = 76;
    public const double GroupGap = 112;
    public const double CanvasPadding = 64;
    public const double RegionPadding = 22;
    // Edge-to-edge distance at which two cards are read as one rough region. Larger than the
    // tree level gap (one relation tree is one region), smaller than the group gap.
    public const double RegionJoinDistance = 84;
    public const double MinCanvasWidth = 1680;
    public const double MinCanvasHeight = 1040;
    public const double TrailingSpace = CardWidth * 1.5;
    public const double MaxCoordinate = 200_000;

    // Relative to the canvas the card is dragged on, as the user specified.
    public const double ExtendRatio = 0.90;
    public const double CrossRatio = 0.95;

    // Canvas units per second. Auto-pan stays below ExtendRatio × extension speed, so holding
    // the pointer at the window edge keeps growing the canvas and never drifts into crossing.
    public const double ExtendSpeed = 900;
    public const double AutoPanSpeed = 700;

    public static Size CardSize => new(CardWidth, CardHeight);

    public static Rect CardRect(Point origin) => new(origin, CardSize);

    public static Size CanvasSize(IEnumerable<Point> origins, Size extension)
    {
        var width = MinCanvasWidth;
        var height = MinCanvasHeight;
        foreach (var origin in origins)
        {
            width = Math.Max(width, origin.X + CardWidth + TrailingSpace);
            height = Math.Max(height, origin.Y + CardHeight + TrailingSpace);
        }
        return new Size(
            Math.Min(MaxCoordinate, Math.Max(width, extension.Width)),
            Math.Min(MaxCoordinate, Math.Max(height, extension.Height)));
    }

    public static Point ClampOrigin(Point origin, Size canvas) => new(
        Math.Clamp(origin.X, 0, Math.Max(0, canvas.Width - CardWidth)),
        Math.Clamp(origin.Y, 0, Math.Max(0, canvas.Height - CardHeight)));

    /// <summary>
    /// Edge resistance for a pointer at <paramref name="pointer"/> (canvas coordinates; it may
    /// lie outside the frame). From 90% the canvas grows; from 95% toward a neighbouring
    /// workspace growth stops and the drop target becomes that workspace. Edges without a
    /// neighbour keep growing instead of offering a crossing.
    /// </summary>
    public static WorkbenchEdgeState EvaluateEdge(
        Point pointer,
        Size canvas,
        bool hasLeftNeighbor,
        bool hasRightNeighbor)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return WorkbenchEdgeState.None;
        var rx = pointer.X / canvas.Width;
        var ry = pointer.Y / canvas.Height;
        if (hasRightNeighbor && rx >= CrossRatio) return new(false, false, 1);
        if (hasLeftNeighbor && rx <= 1 - CrossRatio) return new(false, false, -1);
        return new(rx >= ExtendRatio, ry >= ExtendRatio, 0);
    }

    // ------------------------------------------------------------------ regions

    /// <summary>
    /// Single-link clustering of card rectangles: cards whose edges come within
    /// <see cref="RegionJoinDistance"/> share a dashed region. Lone cards get no frame.
    /// </summary>
    public static IReadOnlyList<WorkbenchRegion> Regions(IReadOnlyList<(string Id, Rect Bounds)> cards)
    {
        var count = cards.Count;
        var parent = new int[count];
        for (var i = 0; i < count; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        var half = RegionJoinDistance / 2;
        for (var i = 0; i < count; i++)
        {
            var a = Inflate(cards[i].Bounds, half);
            for (var j = i + 1; j < count; j++)
            {
                if (!a.IntersectsWith(Inflate(cards[j].Bounds, half))) continue;
                var ri = Find(i);
                var rj = Find(j);
                if (ri != rj) parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var members)) groups[root] = members = new List<int>();
            members.Add(i);
        }

        var regions = new List<WorkbenchRegion>();
        foreach (var members in groups.Values)
        {
            if (members.Count < 2) continue;
            var bounds = cards[members[0]].Bounds;
            foreach (var index in members.Skip(1)) bounds.Union(cards[index].Bounds);
            regions.Add(new WorkbenchRegion(
                Inflate(bounds, RegionPadding),
                members.Select(index => cards[index].Id).ToArray()));
        }
        return regions
            .OrderBy(region => region.Bounds.Top)
            .ThenBy(region => region.Bounds.Left)
            .ToArray();
    }

    // ------------------------------------------------------------------ full arrange

    /// <summary>
    /// Rebuilds every origin. Deterministic for the same input order. <paramref name="targetWidth"/>
    /// is the preferred content width (the caller derives it from the viewport aspect); a group
    /// wider than it still gets its own shelf.
    /// </summary>
    public static Dictionary<string, Point> Arrange(
        IReadOnlyList<WorkbenchLayoutCard> cards,
        IReadOnlyList<(string A, string B)> edges,
        double targetWidth)
    {
        var result = new Dictionary<string, Point>(StringComparer.Ordinal);
        if (cards.Count == 0) return result;

        var blocks = BuildBlocks(cards, edges);
        if (!double.IsFinite(targetWidth) || targetWidth <= 0) targetWidth = PreferredWidth(blocks, 16.0 / 10.0);
        var shelfLimit = Math.Max(targetWidth, blocks.Max(block => block.Size.Width));

        var x = 0.0;
        var y = 0.0;
        var shelfHeight = 0.0;
        foreach (var block in blocks)
        {
            if (x > 0 && x + block.Size.Width > shelfLimit)
            {
                x = 0;
                y += shelfHeight + GroupGap;
                shelfHeight = 0;
            }
            foreach (var (id, local) in block.Origins)
            {
                result[id] = new Point(CanvasPadding + x + local.X, CanvasPadding + y + local.Y);
            }
            x += block.Size.Width + GroupGap;
            shelfHeight = Math.Max(shelfHeight, block.Size.Height);
        }
        return result;
    }

    /// <summary>Content width that makes the packed groups roughly match an aspect ratio.</summary>
    public static double PreferredWidth(IReadOnlyList<WorkbenchLayoutCard> cards,
        IReadOnlyList<(string A, string B)> edges, double aspect) =>
        PreferredWidth(BuildBlocks(cards, edges), aspect);

    private static double PreferredWidth(IReadOnlyList<Block> blocks, double aspect)
    {
        if (blocks.Count == 0) return MinCanvasWidth - 2 * CanvasPadding;
        if (!double.IsFinite(aspect) || aspect <= 0) aspect = 16.0 / 10.0;
        var area = blocks.Sum(block =>
            (block.Size.Width + GroupGap) * (block.Size.Height + GroupGap));
        var width = Math.Sqrt(area * aspect) * 1.1;
        return Math.Max(MinCanvasWidth - 2 * CanvasPadding,
            Math.Max(width, blocks.Max(block => block.Size.Width)));
    }

    private sealed record Block(int Order, IReadOnlyList<(string Id, Point Local)> Origins, Size Size);

    private static List<Block> BuildBlocks(
        IReadOnlyList<WorkbenchLayoutCard> cards,
        IReadOnlyList<(string A, string B)> edges)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < cards.Count; i++) index.TryAdd(cards[i].Id, i);
        var adjacency = Adjacency(cards.Count, index, edges);

        var visited = new bool[cards.Count];
        var components = new List<List<int>>();
        for (var i = 0; i < cards.Count; i++)
        {
            if (visited[i]) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                foreach (var next in adjacency[current])
                {
                    if (visited[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            component.Sort();
            components.Add(component);
        }

        var blocks = new List<Block>();
        // Related papers come first: they are what the user is thinking about.
        foreach (var component in components
                     .Where(component => component.Count > 1)
                     .OrderByDescending(component => component.Count)
                     .ThenBy(component => component[0]))
        {
            blocks.Add(TreeBlock(blocks.Count, component, adjacency, cards));
        }

        var singles = components.Where(component => component.Count == 1).Select(component => component[0]);
        foreach (var group in singles
                     .GroupBy(i => (cards[i].Kind, cards[i].GroupKey))
                     .OrderBy(group => KindOrder(group.Key.Kind))
                     .ThenBy(group => group.Min()))
        {
            blocks.Add(GridBlock(blocks.Count, group.OrderBy(i => i).ToArray(), cards));
        }
        return blocks;
    }

    private static int KindOrder(WorkbenchCardKind kind) => kind switch
    {
        WorkbenchCardKind.Todo => 0,
        WorkbenchCardKind.Note => 1,
        WorkbenchCardKind.Tethered => 2,
        _ => 3
    };

    private static List<int>[] Adjacency(
        int count,
        IReadOnlyDictionary<string, int> index,
        IReadOnlyList<(string A, string B)> edges)
    {
        var adjacency = new List<int>[count];
        for (var i = 0; i < count; i++) adjacency[i] = new List<int>();
        foreach (var (a, b) in edges)
        {
            if (a == null || b == null ||
                !index.TryGetValue(a, out var ia) ||
                !index.TryGetValue(b, out var ib) ||
                ia == ib)
            {
                continue;
            }
            if (!adjacency[ia].Contains(ib)) adjacency[ia].Add(ib);
            if (!adjacency[ib].Contains(ia)) adjacency[ib].Add(ia);
        }
        foreach (var list in adjacency) list.Sort();
        return adjacency;
    }

    private static Block GridBlock(int order, IReadOnlyList<int> members, IReadOnlyList<WorkbenchLayoutCard> cards)
    {
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(members.Count * 1.6)), 1, 8);
        var rows = (int)Math.Ceiling(members.Count / (double)columns);
        var origins = new List<(string, Point)>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            origins.Add((cards[members[i]].Id,
                new Point(column * (CardWidth + CardGap), row * (CardHeight + CardGap))));
        }
        var width = columns * CardWidth + (columns - 1) * CardGap;
        var height = rows * CardHeight + (rows - 1) * CardGap;
        return new Block(order, origins, new Size(width, height));
    }

    /// <summary>
    /// Two-sided mind-map tree. The root is the best-connected paper; its branches are split
    /// between the right and left sides by size, each side is a tidy layered tree, and links
    /// that are not tree edges are simply drawn on top.
    /// </summary>
    private static Block TreeBlock(
        int order,
        IReadOnlyList<int> component,
        IReadOnlyList<List<int>> adjacency,
        IReadOnlyList<WorkbenchLayoutCard> cards)
    {
        var root = component
            .OrderByDescending(i => adjacency[i].Count)
            .ThenBy(i => i)
            .First();

        var children = new Dictionary<int, List<int>>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var list = new List<int>();
            foreach (var next in adjacency[current])
            {
                if (!seen.Add(next)) continue;
                list.Add(next);
                queue.Enqueue(next);
            }
            children[current] = list;
        }

        var leaves = new Dictionary<int, int>();
        int Leaves(int node)
        {
            if (leaves.TryGetValue(node, out var cached)) return cached;
            var value = children[node].Count == 0 ? 1 : children[node].Sum(Leaves);
            leaves[node] = value;
            return value;
        }

        // Greedy balance: biggest branches first, each to the lighter side; then keep the
        // original order within a side so the result is stable as papers are added.
        var right = new List<int>();
        var left = new List<int>();
        var rightWeight = 0;
        var leftWeight = 0;
        foreach (var branch in children[root].OrderByDescending(Leaves).ThenBy(i => i))
        {
            if (rightWeight <= leftWeight)
            {
                right.Add(branch);
                rightWeight += Leaves(branch);
            }
            else
            {
                left.Add(branch);
                leftWeight += Leaves(branch);
            }
        }
        right.Sort();
        left.Sort();

        var slot = CardHeight + CardGap;
        var level = CardWidth + TreeLevelGap;
        var positions = new Dictionary<int, Point>();

        // Stacks the branches of one side downward from topSlot (in card slots).
        void LayoutSide(IReadOnlyList<int> branches, int direction, double topSlot)
        {
            var cursor = topSlot;
            foreach (var branch in branches)
            {
                Place(branch, 1, direction, cursor);
                cursor += Leaves(branch);
            }
        }

        double Place(int node, int depth, int direction, double topSlot)
        {
            var kids = children[node];
            double center;
            if (kids.Count == 0)
            {
                center = topSlot;
            }
            else
            {
                var cursor = topSlot;
                var first = double.NaN;
                var last = 0.0;
                foreach (var kid in kids)
                {
                    var kidCenter = Place(kid, depth + 1, direction, cursor);
                    if (double.IsNaN(first)) first = kidCenter;
                    last = kidCenter;
                    cursor += Leaves(kid);
                }
                center = (first + last) / 2;
            }
            positions[node] = new Point(direction * depth * level, center * slot);
            return center;
        }

        var span = Math.Max(Math.Max(rightWeight, leftWeight), 1);
        LayoutSide(right, 1, (span - rightWeight) / 2.0);
        LayoutSide(left, -1, (span - leftWeight) / 2.0);
        positions[root] = new Point(0, (span - 1) / 2.0 * slot);

        var minX = positions.Values.Min(p => p.X);
        var minY = positions.Values.Min(p => p.Y);
        var maxX = positions.Values.Max(p => p.X);
        var maxY = positions.Values.Max(p => p.Y);
        var origins = component
            .Select(i => (cards[i].Id, new Point(positions[i].X - minX, positions[i].Y - minY)))
            .ToArray();
        return new Block(order, origins,
            new Size(maxX - minX + CardWidth, maxY - minY + CardHeight));
    }

    // ------------------------------------------------------------------ light placement

    /// <summary>
    /// Finds a slot for one unplaced card without moving anything: beside a linked card if
    /// one is placed, otherwise continuing the grid of its kind group, otherwise in open space
    /// far enough from every region that it starts its own.
    /// </summary>
    public static Point PlaceNew(
        IReadOnlyDictionary<string, Rect> placed,
        IReadOnlyList<string> linkedIds,
        IReadOnlyList<string> sameGroupIds,
        double contentWidth)
    {
        var others = placed.Values.ToArray();
        var limit = Math.Max(contentWidth, MinCanvasWidth - 2 * CanvasPadding) + CanvasPadding;

        var anchors = linkedIds
            .Where(placed.ContainsKey)
            .Select(id => placed[id])
            .ToArray();
        foreach (var anchor in anchors)
        {
            foreach (var candidate in AroundAnchor(anchor))
            {
                if (candidate.X >= 0 && candidate.Y >= 0 && IsFree(CardRect(candidate), others, CardGap / 2))
                    return candidate;
            }
        }

        var group = sameGroupIds.Where(placed.ContainsKey).Select(id => placed[id]).ToArray();
        if (group.Length > 0)
        {
            var groupIds = sameGroupIds.Where(placed.ContainsKey).ToHashSet(StringComparer.Ordinal);
            var foreign = placed.Where(pair => !groupIds.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
            var origin = new Point(group.Min(r => r.Left), group.Min(r => r.Top));
            var columns = Math.Max(1, (int)Math.Round((group.Max(r => r.Right) - origin.X + CardGap) / (CardWidth + CardGap)));
            var rows = Math.Max(1, (int)Math.Round((group.Max(r => r.Bottom) - origin.Y + CardGap) / (CardHeight + CardGap)));
            // Holes inside the group's grid first, then one more row, then one more column.
            var slots = new List<(int Row, int Column)>();
            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++) slots.Add((row, column));
            for (var column = 0; column < columns; column++) slots.Add((rows, column));
            for (var row = 0; row <= rows; row++) slots.Add((row, columns));
            foreach (var (row, column) in slots)
            {
                var candidate = new Point(
                    origin.X + column * (CardWidth + CardGap),
                    origin.Y + row * (CardHeight + CardGap));
                var rect = CardRect(candidate);
                if (IsFree(rect, others, CardGap / 2) && IsFree(rect, foreign, RegionJoinDistance + 4))
                    return candidate;
            }
        }

        // Open space: scan rows top to bottom inside the preferred width.
        var stepX = CardWidth + CardGap;
        var stepY = CardHeight + CardGap;
        for (var y = CanvasPadding; y < MaxCoordinate; y += stepY)
        {
            for (var x = CanvasPadding; x + CardWidth <= limit || x == CanvasPadding; x += stepX)
            {
                var candidate = new Point(x, y);
                if (IsFree(CardRect(candidate), others, RegionJoinDistance + 4)) return candidate;
            }
        }
        return new Point(CanvasPadding, CanvasPadding);
    }

    private static IEnumerable<Point> AroundAnchor(Rect anchor)
    {
        var level = CardWidth + TreeLevelGap;
        var slot = CardHeight + CardGap;
        // Mind-map order: right of the anchor, stepping down then up, then the left side.
        for (var ring = 0; ring < 6; ring++)
        {
            foreach (var dy in ring == 0 ? new[] { 0.0 } : new[] { ring * slot, -ring * slot })
                yield return new Point(anchor.Left + level, anchor.Top + dy);
            foreach (var dy in ring == 0 ? new[] { 0.0 } : new[] { ring * slot, -ring * slot })
                yield return new Point(anchor.Left - level, anchor.Top + dy);
        }
        yield return new Point(anchor.Left, anchor.Bottom + CardGap);
        yield return new Point(anchor.Left, anchor.Top - slot);
    }

    /// <summary>
    /// Lightest strength: the card the user dropped is moved to the nearest spot that does not
    /// overlap another card. Nothing else moves.
    /// </summary>
    public static Point ResolveDrop(Point origin, IReadOnlyList<Rect> others, Size canvas)
    {
        origin = ClampOrigin(origin, canvas);
        const double margin = 8;
        if (IsFree(CardRect(origin), others, margin)) return origin;

        const double step = 12;
        for (var ring = 1; ring <= 80; ring++)
        {
            Point? best = null;
            var bestDistance = double.MaxValue;
            for (var i = -ring; i <= ring; i++)
            {
                foreach (var (dx, dy) in new[] { (i, -ring), (i, ring), (-ring, i), (ring, i) })
                {
                    var candidate = ClampOrigin(new Point(origin.X + dx * step, origin.Y + dy * step), canvas);
                    if (!IsFree(CardRect(candidate), others, margin)) continue;
                    var distance = (candidate - origin).LengthSquared;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }
            if (best is { } found) return found;
        }
        return origin;
    }

    public static bool IsFree(Rect rect, IReadOnlyList<Rect> others, double margin)
    {
        var inflated = Inflate(rect, margin);
        foreach (var other in others)
        {
            if (inflated.IntersectsWith(other) && !IsTouchingOnly(inflated, other)) return false;
        }
        return true;
    }

    // Rect.IntersectsWith treats shared edges as intersecting; touching is free space.
    private static bool IsTouchingOnly(Rect a, Rect b) =>
        a.Right <= b.Left || b.Right <= a.Left || a.Bottom <= b.Top || b.Bottom <= a.Top;

    private static Rect Inflate(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }
}
