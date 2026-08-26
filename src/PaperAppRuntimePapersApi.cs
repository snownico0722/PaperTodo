using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// The one provider Runtime addresses its logical Paper instances through PaperId. The API owns no
/// per-Paper backend objects; it only routes lifecycle, presentation and frontend messages.
/// </summary>
internal sealed class PaperAppRuntimePapersApi : IPaperPluginRuntimePapers, IDisposable
{
    private readonly AppController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly string _providerId;
    private readonly Func<bool> _isActive;
    private readonly object _gate = new();
    private readonly HashSet<string> _knownPaperIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Action<PaperPluginRuntimeEvent>> _handlers = [];
    private long _nextHandlerId;
    private bool _disposed;

    public PaperAppRuntimePapersApi(
        AppController controller,
        string providerId,
        Func<bool> isActive)
    {
        _controller = controller;
        _providerId = providerId;
        _isActive = isActive;
        _dispatcher = Application.Current.Dispatcher;
        foreach (var paper in OnUi(() => _controller.GetPluginRuntimePapers(_providerId)))
        {
            _knownPaperIds.Add(paper.PaperId);
        }
    }

    public IReadOnlyList<PaperPluginRuntimePaper> List()
    {
        EnsureUsable();
        return OnUi(() => _controller.GetPluginRuntimePapers(_providerId));
    }

    public PaperPluginRuntimePaper? Get(string paperId)
    {
        EnsureUsable();
        var normalized = NormalizePaperId(paperId);
        return OnUi(() => _controller.GetPluginRuntimePaper(_providerId, normalized));
    }

    public void SetTitle(string paperId, string title)
    {
        EnsureUsable();
        var normalized = NormalizePaperId(paperId);
        OnUi(() => _controller.SetPluginRuntimePaperTitle(
            _providerId,
            normalized,
            title ?? string.Empty));
    }

    public void SetHeaderText(string paperId, string text)
    {
        EnsureUsable();
        var normalized = NormalizePaperId(paperId);
        OnUi(() => _controller.SetPluginRuntimePaperHeader(
            _providerId,
            normalized,
            text ?? string.Empty));
    }

    public void SetCapsulePresentation(
        string paperId,
        PaperCapsulePresentation? presentation)
    {
        EnsureUsable();
        var normalized = NormalizePaperId(paperId);
        OnUi(() => _controller.SetPluginRuntimePaperCapsule(
            _providerId,
            normalized,
            presentation));
    }

    public bool PostToBody(string paperId, JsonElement message)
    {
        EnsureUsable();
        var normalized = NormalizePaperId(paperId);
        var payload = message.Clone();
        return OnUi(() => _controller.PostPluginRuntimeMessageToBody(
            _providerId,
            normalized,
            payload));
    }

    public IDisposable Subscribe(Action<PaperPluginRuntimeEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUsable();
        lock (_gate)
        {
            EnsureUsableLocked();
            var id = ++_nextHandlerId;
            _handlers.Add(id, handler);
            return new Subscription(this, id);
        }
    }

    internal void Reconcile()
    {
        if (_disposed || !_isActive())
        {
            return;
        }

        var current = _controller.GetPluginRuntimePapers(_providerId)
            .Select(value => value.PaperId)
            .ToHashSet(StringComparer.Ordinal);
        string[] added;
        string[] removed;
        lock (_gate)
        {
            if (_disposed || !_isActive())
            {
                return;
            }
            added = current.Except(_knownPaperIds, StringComparer.Ordinal).ToArray();
            removed = _knownPaperIds.Except(current, StringComparer.Ordinal).ToArray();
            _knownPaperIds.Clear();
            _knownPaperIds.UnionWith(current);
        }

        foreach (var paperId in added)
        {
            Publish(new PaperPluginRuntimeEvent(
                PaperPluginRuntimeEventKind.PaperAdded,
                paperId));
        }
        foreach (var paperId in removed)
        {
            Publish(new PaperPluginRuntimeEvent(
                PaperPluginRuntimeEventKind.PaperRemoved,
                paperId));
        }
    }

    internal bool PublishMessage(string paperId, JsonElement message)
    {
        if (_disposed || !_isActive() ||
            _controller.GetPluginRuntimePaper(_providerId, paperId) == null)
        {
            return false;
        }

        Publish(new PaperPluginRuntimeEvent(
            PaperPluginRuntimeEventKind.Message,
            paperId,
            message.Clone()));
        return true;
    }

    private void Publish(PaperPluginRuntimeEvent value)
    {
        Action<PaperPluginRuntimeEvent>[] handlers;
        lock (_gate)
        {
            if (_disposed || !_isActive())
            {
                return;
            }
            handlers = _handlers.Values.ToArray();
        }
        foreach (var handler in handlers)
        {
            try { handler(value); } catch { }
        }
    }

    private static string NormalizePaperId(string paperId)
    {
        var normalized = paperId?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new PaperTodoPluginException(
                "invalid_paper_id",
                "PaperId is required.");
        }
        return normalized;
    }

    private void EnsureUsable()
    {
        lock (_gate)
        {
            EnsureUsableLocked();
        }
    }

    private void EnsureUsableLocked()
    {
        if (_disposed || !_isActive())
        {
            throw new PaperTodoPluginException(
                "runtime_closed",
                "The plugin Runtime is no longer active.");
        }
    }

    private T OnUi<T>(Func<T> action) =>
        _dispatcher.CheckAccess()
            ? action()
            : _dispatcher.Invoke(action);

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.Invoke(action);
    }

    private void Unsubscribe(long id)
    {
        lock (_gate)
        {
            _handlers.Remove(id);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _handlers.Clear();
            _knownPaperIds.Clear();
        }
    }

    private sealed class Subscription(PaperAppRuntimePapersApi owner, long id) : IDisposable
    {
        private PaperAppRuntimePapersApi? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
        }
    }
}
