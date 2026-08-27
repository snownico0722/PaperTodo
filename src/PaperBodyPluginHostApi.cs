using System.Collections.Frozen;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi : IPaperTodoHostApi, IPaperTopBarApi, IPaperPresentationApi, IDisposable
{
    private readonly AppController _controller;
    private readonly PaperCommandService _commands;
    private readonly string? _hostPaperId;
    private readonly string _providerId;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly FrozenSet<string> _permissions;
    private readonly Func<bool> _isSessionCurrent;
    private readonly Func<bool> _canReceiveEvents;
    private readonly List<IDisposable> _subscriptions = [];
    private Action<PaperTopBarActionInvocation>? _topBarActionHandler;
    private bool _disposed;

    public PaperBodyPluginHostApi(
        AppController controller,
        PaperCommandService commands,
        string? hostPaperId,
        string providerId,
        IEnumerable<string> permissions,
        Func<bool> isSessionCurrent,
        Func<bool> canReceiveEvents)
    {
        _controller = controller;
        _commands = commands;
        _hostPaperId = hostPaperId;
        _providerId = providerId;
        _permissions = permissions.ToFrozenSet(StringComparer.Ordinal);
        _isSessionCurrent = isSessionCurrent;
        _canReceiveEvents = canReceiveEvents;
    }

    public IReadOnlySet<string> GrantedPermissions => _permissions;

    public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null)
    {
        Require(PaperTodoPermissionNames.PapersRead);
        return Invoke(() => _commands.ListPapers(type));
    }

    public PaperSnapshot? GetPaper(string paperId)
    {
        Require(PaperTodoPermissionNames.PapersRead);
        return Invoke(() => _commands.GetPaper(RequiredId(paperId, "paperId")));
    }

    public IReadOnlyList<TodoSnapshot> ListTodos(
        string? paperId = null,
        bool includeBlank = false)
    {
        Require(PaperTodoPermissionNames.TodosRead);
        return Invoke(() => _commands.ListTodos(paperId, includeBlank));
    }

    public NoteSnapshot? GetNote(string paperId)
    {
        Require(PaperTodoPermissionNames.NotesRead);
        return Invoke(() => _commands.GetNote(RequiredId(paperId, "paperId")));
    }

    public PaperMutationResult CreatePaper(CreatePaperRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(PaperTodoPermissionNames.PapersCreate);
        var type = request.Type?.Trim().ToLowerInvariant();
        if (type == PaperTypes.Note && !string.IsNullOrEmpty(request.Content))
        {
            Require(PaperTodoPermissionNames.NotesAppend);
        }
        if (type == PaperTypes.Todo && request.Todos is { Count: > 0 } todos)
        {
            Require(PaperTodoPermissionNames.TodosAppend);
            if (todos.Any(item =>
                    item.Done ||
                    item.ReminderAt.HasValue ||
                    !string.IsNullOrWhiteSpace(item.LinkedPaperId)))
            {
                Require(PaperTodoPermissionNames.TodosUpdate);
            }
        }
        return Invoke(() => _commands.CreatePaper(
            request,
            PaperOperationContext.Plugin(_providerId)));
    }

    public AppendTodosResult AppendTodos(AppendTodosRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(PaperTodoPermissionNames.TodosAppend);
        if ((request.Todos ?? []).Any(item =>
                item.Done ||
                item.ReminderAt.HasValue ||
                !string.IsNullOrWhiteSpace(item.LinkedPaperId)))
        {
            Require(PaperTodoPermissionNames.TodosUpdate);
        }
        return Invoke(() => _commands.AppendTodos(
            request,
            PaperOperationContext.Plugin(_providerId)));
    }

    public TodoMutationResult UpdateTodo(UpdateTodoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(PaperTodoPermissionNames.TodosUpdate);
        var result = Invoke(() => _commands.UpdateTodo(
            request,
            PaperOperationContext.Plugin(_providerId)));
        var deleted = !_controller.State.Papers.Any(paper =>
            paper.Type == PaperTypes.Todo &&
            string.Equals(paper.Id, result.PaperId, StringComparison.Ordinal) &&
            paper.Items.Any(item =>
                string.Equals(item.Id, result.TodoId, StringComparison.Ordinal)));
        return result with { Deleted = deleted };
    }

    public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(PaperTodoPermissionNames.TodosUpdate);
        return Invoke(() => _commands.SetTodoReminder(
            request,
            PaperOperationContext.Plugin(_providerId)));
    }

    public NoteMutationResult WriteNote(WriteNoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.Mode == NoteWriteMode.Replace
            ? PaperTodoPermissionNames.NotesReplace
            : PaperTodoPermissionNames.NotesAppend);
        return Invoke(() => _commands.WriteNote(
            request,
            PaperOperationContext.Plugin(_providerId)));
    }

    public DeleteMutationResult DeleteTodo(DeleteTodoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(PaperTodoPermissionNames.TodosDelete);
        return Invoke(() => _commands.DeleteTodo(
            request,
            PaperOperationContext.Plugin(_providerId)));
    }

    public DeleteMutationResult DeletePaper(string paperId)
    {
        Require(PaperTodoPermissionNames.PapersDelete);
        var normalized = RequiredId(paperId, "paperId");
        if (_hostPaperId != null &&
            string.Equals(normalized, _hostPaperId, StringComparison.Ordinal))
        {
            throw Error(
                "cannot_delete_host_paper",
                "A plugin cannot delete the paper that owns its active session.");
        }
        return Invoke(() => _commands.DeletePaper(
            normalized,
            PaperOperationContext.Plugin(_providerId)));
    }

    public void SetActionHandler(Action<PaperTopBarActionInvocation>? handler)
    {
        EnsureUsable();
        _topBarActionHandler = handler;
        if (handler == null)
        {
            _controller.RemovePluginPaperTopBarSession(_sessionId);
        }
    }

    public void SetPaperActions(
        IReadOnlyList<PaperTopBarAction> actions,
        PaperHostTopBarActions hiddenHostActions = PaperHostTopBarActions.None)
    {
        ArgumentNullException.ThrowIfNull(actions);
        EnsureUsable();
        EnsureTopBarHandlerForActions(actions);
        var hostPaperId = _hostPaperId;
        if (string.IsNullOrEmpty(hostPaperId))
        {
            throw Error(
                "host_paper_unavailable",
                "This plugin context is not attached to a paper.");
        }
        _controller.SetPluginPaperTopBarActions(
            _sessionId,
            _providerId,
            hostPaperId,
            actions,
            hiddenHostActions,
            () => !_disposed && _isSessionCurrent(),
            DispatchTopBarAction);
    }

    public void Clear()
    {
        EnsureUsable();
        _topBarActionHandler = null;
        _controller.RemovePluginPaperTopBarSession(_sessionId);
    }

    private void EnsureTopBarHandlerForActions(IReadOnlyList<PaperTopBarAction> actions)
    {
        if (actions.Count > 0 && _topBarActionHandler == null)
        {
            throw Error(
                "topbar_handler_missing",
                "Register a top-bar action handler before contributing top-bar actions.");
        }
    }

    private void DispatchTopBarAction(PaperTopBarActionInvocation invocation)
    {
        if (_disposed || !_isSessionCurrent())
        {
            return;
        }
        _topBarActionHandler?.Invoke(invocation);
    }

    public IDisposable Subscribe(
        PaperTodoEventFilter filter,
        Action<PaperTodoEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUsable();

        var kinds = filter.Kinds is { Count: > 0 }
            ? filter.Kinds.ToHashSet()
            : DefaultObservableKinds();
        if (kinds.Count == 0)
        {
            throw Error(
                "permission_denied",
                "The plugin did not declare an observe permission.");
        }
        foreach (var kind in kinds)
        {
            Require(ObservePermission(kind));
        }

        var normalized = new PaperTodoEventFilter
        {
            Kinds = kinds.ToFrozenSet(),
            PaperIds = filter.PaperIds?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToFrozenSet(StringComparer.Ordinal),
            ExcludeOwnOperations = filter.ExcludeOwnOperations
        };
        var subscription = Invoke(() => _controller.PaperBodyPluginEvents.Subscribe(
            _sessionId,
            _providerId,
            normalized,
            value =>
            {
                if (_disposed || !_isSessionCurrent() || !_canReceiveEvents())
                {
                    return;
                }
                handler(RedactEvent(value));
            }));
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }
        return new TrackedSubscription(this, subscription);
    }

    private PaperTodoEvent RedactEvent(PaperTodoEvent value)
    {
        var canReadPapers = _permissions.Contains(PaperTodoPermissionNames.PapersRead);
        var canReadTodos = _permissions.Contains(PaperTodoPermissionNames.TodosRead);
        return value switch
        {
            PaperCreatedEvent item => item with { Paper = RedactPaper(item.Paper, canReadPapers) },
            PaperChangedEvent item => item with
            {
                Before = RedactPaper(item.Before, canReadPapers),
                After = RedactPaper(item.After, canReadPapers)
            },
            PaperDeletedEvent item => item with { Paper = RedactPaper(item.Paper, canReadPapers) },
            TodoCreatedEvent item => item with { Todo = RedactTodo(item.Todo, canReadPapers, canReadTodos) },
            TodoChangedEvent item => item with
            {
                Before = RedactTodo(item.Before, canReadPapers, canReadTodos),
                After = RedactTodo(item.After, canReadPapers, canReadTodos)
            },
            TodoDeletedEvent item => item with { Todo = RedactTodo(item.Todo, canReadPapers, canReadTodos) },
            NoteChangedEvent item when !canReadPapers => item with { PaperTitle = "" },
            _ => value
        };
    }

    private static PaperSnapshot RedactPaper(PaperSnapshot value, bool canRead) =>
        canRead ? value : value with { Title = "" };

    private static TodoSnapshot RedactTodo(
        TodoSnapshot value,
        bool canReadPapers,
        bool canReadTodos) =>
        value with
        {
            PaperTitle = canReadPapers ? value.PaperTitle : "",
            Text = canReadTodos ? value.Text : "",
            LinkedPaperId = canReadTodos ? value.LinkedPaperId : null,
            LinkedPath = canReadTodos ? value.LinkedPath : null,
            ReminderAt = canReadTodos ? value.ReminderAt : null
        };

    private T Invoke<T>(Func<T> action)
    {
        EnsureUsable();
        try
        {
            return action();
        }
        catch (PaperCommandException ex)
        {
            throw Error(ex.Code, ex.Message);
        }
    }

    private void EnsureUsable()
    {
        if (_disposed || !_isSessionCurrent())
        {
            throw Error("session_closed", "The plugin session is no longer active.");
        }
    }

    private void Require(string permission)
    {
        EnsureUsable();
        if (!_permissions.Contains(permission))
        {
            throw Error(
                "permission_denied",
                $"The plugin did not declare '{permission}'.");
        }
    }

    private static string ObservePermission(PaperTodoEventKind kind) => kind switch
    {
        PaperTodoEventKind.PaperCreated or
        PaperTodoEventKind.PaperChanged or
        PaperTodoEventKind.PaperDeleted => PaperTodoPermissionNames.PapersObserve,
        PaperTodoEventKind.TodoCreated or
        PaperTodoEventKind.TodoChanged or
        PaperTodoEventKind.TodoDeleted => PaperTodoPermissionNames.TodosObserve,
        PaperTodoEventKind.NoteChanged => PaperTodoPermissionNames.NotesObserve,
        _ => throw Error("invalid_params", "Unknown event kind.")
    };

    private HashSet<PaperTodoEventKind> DefaultObservableKinds()
    {
        var result = new HashSet<PaperTodoEventKind>();
        if (_permissions.Contains(PaperTodoPermissionNames.PapersObserve))
        {
            result.UnionWith([
                PaperTodoEventKind.PaperCreated,
                PaperTodoEventKind.PaperChanged,
                PaperTodoEventKind.PaperDeleted]);
        }
        if (_permissions.Contains(PaperTodoPermissionNames.TodosObserve))
        {
            result.UnionWith([
                PaperTodoEventKind.TodoCreated,
                PaperTodoEventKind.TodoChanged,
                PaperTodoEventKind.TodoDeleted]);
        }
        if (_permissions.Contains(PaperTodoPermissionNames.NotesObserve))
        {
            result.Add(PaperTodoEventKind.NoteChanged);
        }
        return result;
    }

    private static string RequiredId(string? value, string name)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length is 0 or > 64)
        {
            throw Error("invalid_params", $"{name} is invalid.");
        }
        return normalized;
    }

    private static PaperTodoPluginException Error(string code, string message) =>
        new(code, message);

    private void RemoveTracked(IDisposable inner)
    {
        lock (_subscriptions)
        {
            _subscriptions.Remove(inner);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _topBarActionHandler = null;
        try { _controller.RemovePluginPaperTopBarSession(_sessionId); } catch { }
        IDisposable[] subscriptions;
        lock (_subscriptions)
        {
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
        {
            try { subscription.Dispose(); } catch { }
        }
        try { _controller.PaperBodyPluginEvents.RemoveSession(_sessionId); } catch { }
    }

    private sealed class TrackedSubscription : IDisposable
    {
        private PaperBodyPluginHostApi? _owner;
        private IDisposable? _inner;

        public TrackedSubscription(PaperBodyPluginHostApi owner, IDisposable inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public void Dispose()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            var owner = Interlocked.Exchange(ref _owner, null);
            if (inner == null) return;
            try { inner.Dispose(); }
            finally { owner?.RemoveTracked(inner); }
        }
    }
}
