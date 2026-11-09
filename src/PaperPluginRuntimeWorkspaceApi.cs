using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Hides paper-session presentation capabilities from plugin runtimes while reusing the same reviewed
/// Workspace implementation and permission/event semantics. Unlike a paper body context, a Runtime
/// has no View.Dispatcher, so this facade marshals synchronous Workspace calls to the UI dispatcher
/// before entering PaperCommandService. Protocol 2.1 Runtime contribution surfaces are exposed by
/// capability interfaces on this same lease so teardown has one owner.
/// </summary>
internal sealed class PaperPluginRuntimeWorkspaceApi :
    IPaperTodoHostApi,
    IPaperReminderNotificationControl,
    IPaperPluginRuntimeTodoActions,
    IPaperPluginRuntimeTopBarLabels,
    IPaperWorkspacePresentationApi,
    IPaperSettingsApi,
    IPaperPluginPaperActions,
    IPaperNoteAssetsApi,
    IPaperPluginPopups,
    IDisposable
{
    private readonly AppController _controller;
    private readonly PaperBodyPluginHostApi _inner;
    private readonly Dispatcher _dispatcher;
    private readonly string _providerId;
    private readonly Func<bool> _isActive;
    private readonly Guid _contributionOwnerId = Guid.NewGuid();
    private Action<PaperTodoActionInvocation>? _todoActionHandler;
    private Action<PaperActionInvocation>? _paperActionHandler;
    private bool _disposed;

    public PaperPluginRuntimeWorkspaceApi(
        AppController controller,
        string providerId,
        IEnumerable<string> permissions,
        Func<bool> isActive)
    {
        _controller = controller;
        _providerId = providerId;
        _isActive = isActive;
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
    public bool SuppressDefaultReminderNotification(Guid notificationId) =>
        OnUi(() => _inner.SuppressDefaultReminderNotification(notificationId));
    public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler) =>
        OnUi(() => _inner.Subscribe(filter, handler));

    public DataReloadResult ReloadData() => OnUi(_inner.ReloadData);
    public DataReloadResult MergeData(string baselineJson) => OnUi(() => _inner.MergeData(baselineJson));
    public DataReloadStatus GetDataReloadStatus() => OnUi(_inner.GetDataReloadStatus);

    public PaperPresentationResult ShowPaper(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).ShowPaper(paperId, activate); });

    public PaperPresentationResult HidePaper(string paperId) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).HidePaper(paperId); });

    public PaperPresentationResult TogglePaperVisibility(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).TogglePaperVisibility(paperId, activate); });

    public PaperPresentationResult ExpandPaper(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).ExpandPaper(paperId, activate); });

    public PaperPresentationResult CollapsePaper(string paperId) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).CollapsePaper(paperId); });

    public PaperPresentationResult TogglePaperCollapsed(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).TogglePaperCollapsed(paperId, activate); });

    public PaperPresentationResult ActivatePaper(string paperId) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).ActivatePaper(paperId); });

    IReadOnlyList<PaperSettingSnapshot> IPaperSettingsApi.List(string? category) =>
        OnUi(() => { EnsureUsable(); return ((IPaperSettingsApi)_inner).List(category); });
    PaperSettingSnapshot IPaperSettingsApi.Get(string id) =>
        OnUi(() => { EnsureUsable(); return ((IPaperSettingsApi)_inner).Get(id); });
    PaperSettingChangeResult IPaperSettingsApi.Set(string id, JsonElement value)
    {
        var copy = value.ValueKind == JsonValueKind.Undefined ? value : value.Clone();
        return OnUi(() => { EnsureUsable(); return ((IPaperSettingsApi)_inner).Set(id, copy); });
    }

    public PaperNoteImage ReadImage(string paperId, string imageId) => OnUi(() =>
    {
        EnsureUsable();
        return _inner.ReadImage(paperId, imageId);
    });

    void IPaperPluginPaperActions.SetActionHandler(Action<PaperActionInvocation>? handler) => OnUi(() =>
    {
        EnsureUsable();
        _paperActionHandler = handler;
        if (handler == null) _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
    });

    void IPaperPluginPaperActions.SetActions(string paperId, IReadOnlyList<PaperAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var snapshot = actions.ToArray();
        OnUi(() =>
        {
            EnsureUsable();
            EnsurePermission(PaperTodoPermissionNames.PapersRead, "Paper actions require papers.read.");
            if (snapshot.Length > 0 && _paperActionHandler == null)
                throw new PaperTodoPluginException("paper_action_handler_missing", "Register a Paper action handler first.");
            _controller.SetPluginPaperActions(_contributionOwnerId, _providerId, paperId, snapshot,
                () => !_disposed && _isActive(), invocation => _paperActionHandler?.Invoke(invocation));
        });
    }

    void IPaperPluginPaperActions.Clear(string paperId) => OnUi(() =>
    {
        EnsureUsable();
        _controller.ClearPluginPaperActions(_contributionOwnerId, paperId);
    });
    void IPaperPluginPaperActions.Clear() => OnUi(() =>
    {
        EnsureUsable();
        _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
    });

    public IPaperPluginPopup Open(PaperPopupPosition position, PaperPluginPopupOptions options,
        Func<PaperPluginPopupContext, IPaperPluginPopupContent> createContent) =>
        OnUi(() => { EnsureUsable(); return _inner.Open(position, options, createContent); });
    public void Close() => OnUi(() => { EnsureUsable(); _inner.Close(); });

    internal void ResetExtensionUi() => OnUi(() =>
    {
        _paperActionHandler = null;
        _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
        _inner.ResetExtensionUi();
    });

    void IPaperPluginRuntimeTodoActions.SetActionHandler(
        Action<PaperTodoActionInvocation>? handler) =>
        OnUi(() =>
        {
            EnsureUsable();
            _todoActionHandler = handler;
            if (handler == null)
            {
                _controller.RemovePluginTodoActionsOwner(
                    _contributionOwnerId,
                    _providerId);
            }
        });

    void IPaperPluginRuntimeTodoActions.SetActions(
        string paperId,
        string todoId,
        IReadOnlyList<PaperTodoAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var snapshot = actions.ToArray();
        OnUi(() =>
        {
            EnsureUsable();
            EnsurePermission(
                PaperTodoPermissionNames.TodosRead,
                "Todo action contributions require the todos.read permission because invocation includes a Todo snapshot.");
            if (snapshot.Length > 0 && _todoActionHandler == null)
            {
                throw new PaperTodoPluginException(
                    "todo_action_handler_missing",
                    "Register a Todo action handler before contributing Todo actions.");
            }
            _controller.SetPluginTodoActions(
                _contributionOwnerId,
                _providerId,
                paperId,
                todoId,
                snapshot,
                () => !_disposed && _isActive(),
                DispatchTodoAction);
        });
    }

    void IPaperPluginRuntimeTodoActions.Clear(string paperId, string todoId) =>
        OnUi(() =>
        {
            EnsureUsable();
            _controller.ClearPluginTodoActions(
                _contributionOwnerId,
                _providerId,
                paperId,
                todoId);
        });

    void IPaperPluginRuntimeTodoActions.Clear() =>
        OnUi(() =>
        {
            EnsureUsable();
            _controller.RemovePluginTodoActionsOwner(
                _contributionOwnerId,
                _providerId);
        });

    void IPaperPluginRuntimeTopBarLabels.SetLabels(
        string paperId,
        IReadOnlyList<PaperTopBarLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var snapshot = labels.ToArray();
        OnUi(() =>
        {
            EnsureUsable();
            EnsurePermission(
                PaperTodoPermissionNames.PapersRead,
                "Top-bar label contributions require the papers.read permission.");
            _controller.SetPluginTopBarLabels(
                _contributionOwnerId,
                _providerId,
                paperId,
                snapshot,
                () => !_disposed && _isActive());
        });
    }

    void IPaperPluginRuntimeTopBarLabels.Clear(string paperId) =>
        OnUi(() =>
        {
            EnsureUsable();
            _controller.ClearPluginTopBarLabels(
                _contributionOwnerId,
                _providerId,
                paperId);
        });

    void IPaperPluginRuntimeTopBarLabels.Clear() =>
        OnUi(() =>
        {
            EnsureUsable();
            _controller.RemovePluginTopBarLabelsOwner(
                _contributionOwnerId,
                _providerId);
        });

    private void DispatchTodoAction(PaperTodoActionInvocation invocation)
    {
        if (_disposed || !_isActive())
        {
            return;
        }
        _todoActionHandler?.Invoke(invocation);
    }

    private void EnsurePermission(string permission, string message)
    {
        if (!GrantedPermissions.Contains(permission))
        {
            throw new PaperTodoPluginException("permission_denied", message);
        }
    }

    private void EnsureUsable()
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _todoActionHandler = null;
        try
        {
            if (_dispatcher.CheckAccess())
            {
                RemoveContributionsAndDisposeInner();
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.Invoke(RemoveContributionsAndDisposeInner);
            }
        }
        catch
        {
            // App shutdown owns final process teardown; do not turn dispatcher shutdown into a
            // second plugin-runtime failure.
        }
    }

    private void RemoveContributionsAndDisposeInner()
    {
        ResetExtensionUi();
        _controller.RemovePluginTodoActionsOwner(_contributionOwnerId, _providerId);
        _controller.RemovePluginTopBarLabelsOwner(_contributionOwnerId, _providerId);
        _inner.Dispose();
    }
}
