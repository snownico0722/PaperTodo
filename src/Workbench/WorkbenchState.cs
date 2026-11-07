namespace PaperTodo;

/// <summary>Canvas metadata owned by the workspace materials Note; desktop geometry stays on each Paper.</summary>
public sealed class WorkbenchState
{
    public Dictionary<string, WorkbenchCardState> Cards { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkbenchGroupState> Groups { get; set; } = new(StringComparer.Ordinal);
    public List<WorkbenchConnection> Connections { get; set; } = [];

    internal WorkbenchState Copy() => new()
    {
        Cards = Cards.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Groups = Groups.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Connections = Connections.Select(c => c.Copy()).ToList()
    };

    internal void Normalize(IEnumerable<string> cardIds)
    {
        var ids = cardIds.ToHashSet(StringComparer.Ordinal);
        Cards ??= new(StringComparer.Ordinal);
        foreach (var pair in Cards.ToArray())
        {
            var card = pair.Value;
            if (!ids.Contains(pair.Key) || card is null ||
                !double.IsFinite(card.X) || !double.IsFinite(card.Y) ||
                Math.Abs(card.X) > 10_000_000 || Math.Abs(card.Y) > 10_000_000)
            {
                Cards.Remove(pair.Key);
                continue;
            }
            var material = pair.Key.StartsWith(WorkspaceMaterialItem.IdPrefix, StringComparison.Ordinal);
            card.Width = WorkbenchCardState.ValidSize(card.Width, 320, material ? 120 : 200, 1600);
            card.Height = WorkbenchCardState.ValidSize(card.Height, 360, material ? 64 : 160, 1600);
        }

        Groups ??= new(StringComparer.Ordinal);
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in Groups.ToArray())
        {
            var group = pair.Value;
            if (group == null) { Groups.Remove(pair.Key); continue; }
            group.Id = pair.Key;
            group.Title ??= "";
            group.Members = (group.Members ?? []).Where(id => ids.Contains(id) && grouped.Add(id)).ToList();
            if (group.Members.Count == 0) Groups.Remove(pair.Key);
        }

        Connections ??= [];
        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var distinct = new HashSet<(string From, string To, string Label, bool IsBranch)>();
        var valid = new List<WorkbenchConnection>();
        foreach (var connection in Connections)
        {
            if (connection == null || !ids.Contains(connection.From) || !ids.Contains(connection.To) ||
                connection.From == connection.To) continue;
            connection.Label ??= "";
            if (connection.IsBranch)
            {
                // A free relationship remains valid even when it cannot be a tree branch.
                var cycle = false;
                for (var ancestor = connection.From; parents.TryGetValue(ancestor, out ancestor);)
                    if (ancestor == connection.To) { cycle = true; break; }
                if (cycle || parents.TryGetValue(connection.To, out var parent) && parent != connection.From)
                    connection.IsBranch = false;
                else parents[connection.To] = connection.From;
            }
            if (!distinct.Add((connection.From, connection.To, connection.Label, connection.IsBranch))) continue;
            if (string.IsNullOrWhiteSpace(connection.Id) || !connectionIds.Add(connection.Id))
            {
                var index = valid.Count;
                do { connection.Id = "connection:" + index++; } while (!connectionIds.Add(connection.Id));
            }
            valid.Add(connection);
        }
        Connections = valid;
    }

    internal void RenameCard(string oldId, string newId)
    {
        if (oldId == newId) return;
        Cards ??= new(StringComparer.Ordinal);
        Groups ??= new(StringComparer.Ordinal);
        Connections ??= [];
        if (Cards.Remove(oldId, out var placement)) Cards.TryAdd(newId, placement);
        foreach (var group in Groups.Values)
        {
            if (group?.Members == null) continue;
            for (var i = 0; i < group.Members.Count; i++)
                if (group.Members[i] == oldId) group.Members[i] = newId;
        }
        foreach (var connection in Connections)
        {
            if (connection == null) continue;
            if (connection.From == oldId) connection.From = newId;
            if (connection.To == oldId) connection.To = newId;
        }
    }

    // Saving repairs non-serializable numbers only; it does not prune membership or links.
    internal void PrepareForSave()
    {
        if (Cards == null) return;
        foreach (var card in Cards.Values)
        {
            if (card == null) continue;
            if (!double.IsFinite(card.X)) card.X = 0;
            if (!double.IsFinite(card.Y)) card.Y = 0;
            if (!double.IsFinite(card.Width)) card.Width = 320;
            if (!double.IsFinite(card.Height)) card.Height = 360;
        }
    }
}

public sealed class WorkbenchGroupState
{
    public string Id { get; set; } = "group:" + Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public List<string> Members { get; set; } = [];
    public bool Collapsed { get; set; }

    internal WorkbenchGroupState Copy() => new()
    {
        Id = Id, Title = Title, Members = Members.ToList(), Collapsed = Collapsed
    };
}

public sealed class WorkbenchConnection
{
    public string Id { get; set; } = "connection:" + Guid.NewGuid().ToString("N");
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsBranch { get; set; }

    internal WorkbenchConnection Copy() => (WorkbenchConnection)MemberwiseClone();
}

public sealed class WorkbenchCardState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 360;
    public bool Pinned { get; set; }
    public bool Preferred { get; set; }
    // Canvas-only folding never changes the desktop paper's capsule state.
    // Height remains the last expanded height so unfolding is lossless.
    public bool Collapsed { get; set; }

    internal const double CollapsedHeight = 48;
    internal WorkbenchRect Bounds => new(X, Y, Width, Height);
    internal WorkbenchRect LayoutBounds => new(X, Y, Width, Collapsed ? CollapsedHeight : Height);
    internal WorkbenchCardState Copy() => (WorkbenchCardState)MemberwiseClone();
    internal static double ValidSize(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

internal readonly record struct WorkbenchPoint(double X, double Y)
{
    public static WorkbenchPoint operator +(WorkbenchPoint a, WorkbenchPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static WorkbenchPoint operator -(WorkbenchPoint a, WorkbenchPoint b) => new(a.X - b.X, a.Y - b.Y);
    public double LengthSquared => X * X + Y * Y;
}

internal readonly record struct WorkbenchRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public WorkbenchPoint Center => new(X + Width / 2, Y + Height / 2);
    public WorkbenchPoint Location => new(X, Y);
    public WorkbenchRect At(WorkbenchPoint p) => new(p.X, p.Y, Width, Height);
    public WorkbenchRect Offset(WorkbenchPoint p) => this with { X = X + p.X, Y = Y + p.Y };
    public WorkbenchRect Inflate(double padding) => new(X - padding, Y - padding, Width + 2 * padding, Height + 2 * padding);
    public bool Overlaps(WorkbenchRect other, double gap = 0) =>
        Right + gap > other.X + .001 && other.Right + gap > X + .001 &&
        Bottom + gap > other.Y + .001 && other.Bottom + gap > Y + .001;
    public bool Contains(WorkbenchPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public static WorkbenchRect Union(IEnumerable<WorkbenchRect> rectangles)
    {
        var array = rectangles.ToArray();
        if (array.Length == 0) return new(0, 0, 640, 420);
        var x = array.Min(r => r.X); var y = array.Min(r => r.Y);
        return new(x, y, array.Max(r => r.Right) - x, array.Max(r => r.Bottom) - y);
    }
}
