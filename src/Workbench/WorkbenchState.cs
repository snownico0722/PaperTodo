namespace PaperTodo;

/// <summary>Workspace-local spatial metadata; content and desktop geometry remain on PaperData.</summary>
public sealed class WorkbenchState
{
    public Dictionary<string, WorkbenchCardState> Cards { get; set; } = new(StringComparer.Ordinal);

    internal WorkbenchState Copy() => new()
    {
        Cards = Cards.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal)
    };

    internal void Normalize(IEnumerable<string> paperIds)
    {
        var ids = paperIds.ToHashSet(StringComparer.Ordinal);
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
            card.Width = WorkbenchCardState.ValidSize(card.Width, 320, 200, 1600);
            card.Height = WorkbenchCardState.ValidSize(card.Height, 360, 160, 1600);
        }
    }
}

public sealed class WorkbenchCardState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 360;
    public bool Pinned { get; set; }
    public bool Preferred { get; set; }

    internal WorkbenchRect Bounds => new(X, Y, Width, Height);
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
