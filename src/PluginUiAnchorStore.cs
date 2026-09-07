using System.Windows;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>UI-thread only. Tokens never retain a Button, menu item or owner Window strongly.</summary>
internal sealed class PluginUiAnchorStore(Func<long>? clock = null)
{
    private const long LifetimeMilliseconds = 30_000;
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Dictionary<string, Entry> _entries = [];

    internal sealed record Entry(Guid LeaseId, string PaperId, long CreatedAt,
        WeakReference<FrameworkElement> Target, WeakReference<Window> Owner,
        Rect Placement, Point Origin, Size TargetSize, DpiScale Dpi)
    {
        internal bool TryResolve(out FrameworkElement target, out Window owner)
        {
            target = null!;
            owner = null!;
            if (!Target.TryGetTarget(out target!) || !Owner.TryGetTarget(out owner!) ||
                !owner.IsVisible || !target.IsLoaded || !target.IsVisible ||
                PresentationSource.FromVisual(target) == null) return false;
            try
            {
                var current = target.PointToScreen(new Point());
                return (current - Origin).Length < 0.5 &&
                    Math.Abs(target.ActualWidth - TargetSize.Width) < 0.01 &&
                    Math.Abs(target.ActualHeight - TargetSize.Height) < 0.01 &&
                    VisualTreeHelper.GetDpi(target).Equals(Dpi);
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal PaperUiAnchor? Capture(Guid leaseId, string paperId,
        FrameworkElement? source, Window? owner, bool transientSource)
    {
        if (source == null || owner == null || !owner.IsVisible ||
            !source.IsVisible || source.ActualWidth <= 0 || source.ActualHeight <= 0 ||
            PresentationSource.FromVisual(source) == null) return null;
        source.Dispatcher.VerifyAccess();
        var now = _clock();
        foreach (var key in _entries.Where(pair => now - pair.Value.CreatedAt >= LifetimeMilliseconds)
                     .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
        // A callback flood cannot retain an unbounded set of tokens until the timeout.
        foreach (var key in _entries.Where(pair => pair.Value.LeaseId == leaseId)
                     .OrderByDescending(pair => pair.Value.CreatedAt).Skip(15)
                     .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
        try
        {
            var target = transientSource ? owner : source;
            var placement = transientSource
                ? new Rect(owner.PointFromScreen(source.PointToScreen(new Point())),
                    owner.PointFromScreen(source.PointToScreen(new Point(source.ActualWidth, source.ActualHeight))))
                : new Rect(new Point(), new Size(source.ActualWidth, source.ActualHeight));
            var token = new PaperUiAnchor(Guid.NewGuid().ToString("N"));
            _entries.Add(token.Id, new Entry(leaseId, paperId, now,
                new(target), new(owner), placement, target.PointToScreen(new Point()),
                new(target.ActualWidth, target.ActualHeight), VisualTreeHelper.GetDpi(target)));
            return token;
        }
        catch (InvalidOperationException) { return null; }
    }

    internal Entry Resolve(Guid leaseId, PaperUiAnchor anchor)
    {
        if (anchor == null || string.IsNullOrEmpty(anchor.Id) ||
            !_entries.TryGetValue(anchor.Id, out var entry) || entry.LeaseId != leaseId)
            throw Unavailable();
        if (_clock() - entry.CreatedAt >= LifetimeMilliseconds || !entry.TryResolve(out _, out _))
        {
            _entries.Remove(anchor.Id);
            throw Unavailable();
        }
        return entry;
    }

    internal void RemoveOwner(Guid leaseId)
    {
        foreach (var key in _entries.Where(pair => pair.Value.LeaseId == leaseId)
                     .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
    }
    internal void RemovePaper(string paperId)
    {
        foreach (var key in _entries.Where(pair => pair.Value.PaperId == paperId)
                     .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
    }
    private static PaperTodoPluginException Unavailable() =>
        new("anchor_unavailable", "The UI anchor expired, moved, or no longer belongs to this plugin lease.");
}
