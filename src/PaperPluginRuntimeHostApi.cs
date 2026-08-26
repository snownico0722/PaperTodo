using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Hides paper-session presentation capabilities from plugin runtimes while reusing the same reviewed
/// Workspace implementation and permission/event semantics. Unlike a paper body context, an app
/// runtime has no View.Dispatcher, so this facade marshals synchronous Workspace calls to the UI
/// dispatcher before entering PaperCommandService.
/// </summary>
internal sealed class PaperPluginRuntimeWorkspaceApi : IPaperTodoHostApi, IDisposable
{
    private readonly PaperBodyPluginHostApi _inner;
    private readonly Dispatcher _dispatcher;

    public PaperPluginRuntimeWorkspaceApi(
        AppController controller,
        string providerId,
        IEnumerable<string> permissions,
        Func<bool> isActive)
    {
        _dispatcher = Application.Current.Dispatcher;
        _inner = new PaperBodyPluginHostApi(
            controller,
            controller.PaperCommands,
            hostPaperId: null,
            providerId,
            permissions,
            isSessionCurrent: isActive,
            canReceiveEvents: isActive);
    }

    public IReadOnlySet<string> GrantedPermissions => _inner.GrantedPermissions;
    public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null) =>
        OnUi(() => _inner.ListPapers(type));
    public PaperSnapshot? GetPaper(string paperId) =>
        OnUi(() => _inner.GetPaper(paperId));
    public IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false) =>
        OnUi(() => _inner.ListTodos(paperId, includeBlank));
    public NoteSnapshot? GetNote(string paperId) =>
        OnUi(() => _inner.GetNote(paperId));
    public PaperMutationResult CreatePaper(CreatePaperRequest request) =>
        OnUi(() => _inner.CreatePaper(request));
    public AppendTodosResult AppendTodos(AppendTodosRequest request) =>
        OnUi(() => _inner.AppendTodos(request));
    public TodoMutationResult UpdateTodo(UpdateTodoRequest request) =>
        OnUi(() => _inner.UpdateTodo(request));
    public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request) =>
        OnUi(() => _inner.SetTodoReminder(request));
    public NoteMutationResult WriteNote(WriteNoteRequest request) =>
        OnUi(() => _inner.WriteNote(request));
    public DeleteMutationResult DeleteTodo(DeleteTodoRequest request) =>
        OnUi(() => _inner.DeleteTodo(request));
    public DeleteMutationResult DeletePaper(string paperId) =>
        OnUi(() => _inner.DeletePaper(paperId));
    public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler) =>
        OnUi(() => _inner.Subscribe(filter, handler));

    private T OnUi<T>(Func<T> action) =>
        _dispatcher.CheckAccess()
            ? action()
            : _dispatcher.Invoke(action);

    public void Dispose()
    {
        try
        {
            if (_dispatcher.CheckAccess())
            {
                _inner.Dispose();
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.Invoke(_inner.Dispose);
            }
        }
        catch
        {
            // App shutdown owns final process teardown; do not turn dispatcher shutdown into a
            // second plugin-runtime failure.
        }
    }
}

/// <summary>
/// Provider-scoped read-only settings facade. PaperBodyPluginDataStore already serializes access,
/// so worker-thread reads do not need an extra UI-dispatch hop. The active-runtime predicate keeps
/// a retained facade from becoming an accidental post-Dispose settings handle.
/// </summary>
internal sealed class PaperPluginRuntimeSettingsApi : IPaperPluginRuntimeSettings, IDisposable
{
    private readonly PaperBodyPluginDataStore _dataStore;
    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly Func<bool> _isActive;
    private readonly object _gate = new();
    private readonly Dictionary<long, Action<string>> _handlers = [];
    private long _nextHandlerId;
    private bool _disposed;

    public PaperPluginRuntimeSettingsApi(
        PaperBodyPluginDataStore dataStore,
        PaperBodyPluginDescriptor descriptor,
        Func<bool> isActive)
    {
        _dataStore = dataStore;
        _descriptor = descriptor;
        _isActive = isActive;
    }

    public string Json
    {
        get
        {
            EnsureUsable();
            return _dataStore.GetSettingsJson(_descriptor);
        }
    }

    public IDisposable Subscribe(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            EnsureUsableLocked();
            var id = ++_nextHandlerId;
            _handlers.Add(id, handler);
            return new Subscription(this, id);
        }
    }

    internal void PublishChanged(string json)
    {
        Action<string>[] handlers;
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
            try { handler(json); } catch { }
        }
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
            _disposed = true;
            _handlers.Clear();
        }
    }

    private sealed class Subscription(PaperPluginRuntimeSettingsApi owner, long id) : IDisposable
    {
        private PaperPluginRuntimeSettingsApi? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}

internal sealed class PaperPluginRuntimeGlobalTopBarApi : IPaperGlobalTopBarApi, IDisposable
{
    private readonly AppController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly Guid _runtimeId;
    private readonly string _providerId;
    private readonly Func<bool> _isActive;
    private Action<PaperTopBarActionInvocation>? _handler;
    private bool _disposed;

    public PaperPluginRuntimeGlobalTopBarApi(
        AppController controller,
        Guid runtimeId,
        string providerId,
        Func<bool> isActive)
    {
        _controller = controller;
        _dispatcher = Application.Current.Dispatcher;
        _runtimeId = runtimeId;
        _providerId = providerId;
        _isActive = isActive;
    }

    public void SetActionHandler(Action<PaperTopBarActionInvocation>? handler) =>
        OnUi(() =>
        {
            EnsureUsable();
            _handler = handler;
            if (handler == null)
            {
                _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId);
            }
        });

    public void SetActions(IReadOnlyList<PaperTopBarAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var snapshot = actions.ToArray();
        OnUi(() =>
        {
            EnsureUsable();
            if (snapshot.Length > 0 && _handler == null)
            {
                throw new PaperTodoPluginException(
                    "topbar_handler_missing",
                    "Register a global top-bar action handler before contributing actions.");
            }
            _controller.SetPluginGlobalTopBarActions(
                _runtimeId,
                _providerId,
                snapshot,
                () => !_disposed && _isActive(),
                Dispatch);
        });
    }

    public void Clear() =>
        OnUi(() =>
        {
            EnsureUsable();
            _handler = null;
            _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId);
        });

    private void Dispatch(PaperTopBarActionInvocation invocation)
    {
        if (_disposed || !_isActive())
        {
            return;
        }
        _handler?.Invoke(invocation);
    }

    private void EnsureUsable()
    {
        if (_disposed || !_isActive())
        {
            throw new PaperTodoPluginException(
                "runtime_closed",
                "The plugin runtime is no longer active.");
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _handler = null;
        try
        {
            if (_dispatcher.CheckAccess())
            {
                _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId);
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.Invoke(() =>
                    _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId));
            }
        }
        catch
        {
        }
    }
}
