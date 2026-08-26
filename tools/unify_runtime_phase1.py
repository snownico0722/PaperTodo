from pathlib import Path
import re


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace(path, old, new, count=1):
    text = read(path)
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f'{path}: expected {count} occurrences, found {actual}: {old[:100]!r}')
    write(path, text.replace(old, new, count))


# 1) Provider Runtime gets its own persistent state document.
path = 'src/PaperBodyPluginDataStore.cs'
replace(path,
'''        public Dictionary<string, JsonElement> Settings { get; set; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, PaperDataState> Papers { get; set; } =
            new(StringComparer.Ordinal);''',
'''        public Dictionary<string, JsonElement> Settings { get; set; } =
            new(StringComparer.Ordinal);
        public PaperDataState? Runtime { get; set; }
        public Dictionary<string, PaperDataState> Papers { get; set; } =
            new(StringComparer.Ordinal);''')

replace(path,
'''    public PaperBodyStoredState ReadPaperState(string providerId, string paperId) =>
        TryReadPaperState(providerId, paperId, out var state)
            ? state
            : new PaperBodyStoredState();

    public bool TryReadPaperState(''',
'''    public PaperBodyStoredState ReadRuntimeState(string providerId) =>
        TryReadRuntimeState(providerId, out var state)
            ? state
            : new PaperBodyStoredState();

    public bool TryReadRuntimeState(
        string providerId,
        out PaperBodyStoredState state)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(providerId);
            var stored = document.Runtime;
            if (stored == null || stored.Data.ValueKind == JsonValueKind.Undefined)
            {
                state = new PaperBodyStoredState();
                return false;
            }
            state = new PaperBodyStoredState
            {
                Version = Math.Max(1, stored.StateVersion),
                Json = stored.Data.GetRawText()
            };
            return true;
        }
    }

    public void SaveRuntimeState(
        string providerId,
        int stateVersion,
        string? json)
    {
        var normalized = NormalizeStateJson(json);
        using var parsed = JsonDocument.Parse(normalized);
        var value = parsed.RootElement.Clone();

        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(providerId);
            stateVersion = Math.Max(1, stateVersion);
            if (document.Runtime is { } existing &&
                existing.StateVersion == stateVersion &&
                JsonElementEquals(existing.Data, value))
            {
                return;
            }
            document.Runtime = new PaperDataState
            {
                StateVersion = stateVersion,
                Data = value
            };
            ScheduleSave(providerId);
        }
    }

    public PaperBodyStoredState ReadPaperState(string providerId, string paperId) =>
        TryReadPaperState(providerId, paperId, out var state)
            ? state
            : new PaperBodyStoredState();

    public bool TryReadPaperState(''')

# 2) Body/Mini gets a thin command route to the one provider Runtime.
path = 'PaperTodo.Plugin.Abstractions/PaperBodyPluginContracts.cs'
text = read(path)
if not text.startswith('using System.Text.Json;'):
    text = 'using System.Text.Json;\n' + text
write(path, text)
replace(path,
'''    public required IPaperTodoHostApi Workspace { get; init; }
    public IPaperTopBarApi TopBar => Workspace as IPaperTopBarApi''',
'''    public required IPaperTodoHostApi Workspace { get; init; }
    public required IPaperPluginRuntimeClient Runtime { get; init; }
    public IPaperTopBarApi TopBar => Workspace as IPaperTopBarApi''')
replace(path,
'''    void OnDpiChanged() { }

    // Host-rendered global settings changed for this plugin.''',
'''    void OnDpiChanged() { }

    // Message sent by the one provider Runtime to this Paper frontend.
    bool OnRuntimeMessage(JsonElement message) => false;

    // Host-rendered global settings changed for this plugin.''')

# 3) Observable Runtime settings facade.
path = 'src/PaperAppRuntimeHostApi.cs'
text = read(path)
start = text.index('internal sealed class PaperAppRuntimeSettingsApi')
end = text.index('\ninternal sealed class PaperAppRuntimeGlobalTopBarApi', start)
settings_class = r'''internal sealed class PaperAppRuntimeSettingsApi : IPaperAppRuntimeSettings, IDisposable
{
    private readonly PaperBodyPluginDataStore _dataStore;
    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly Func<bool> _isActive;
    private readonly object _gate = new();
    private readonly Dictionary<long, Action<string>> _handlers = [];
    private long _nextHandlerId;
    private bool _disposed;

    public PaperAppRuntimeSettingsApi(
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

    private sealed class Subscription(PaperAppRuntimeSettingsApi owner, long id) : IDisposable
    {
        private PaperAppRuntimeSettingsApi? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}
'''
write(path, text[:start] + settings_class + text[end:])

# 4) One AppRuntime lease now owns settings/state/paper routing APIs.
path = 'src/AppController.PluginAppRuntime.cs'
replace(path,
'''        public required PaperAppRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperAppRuntimeGlobalTopBarApi GlobalTopBar { get; init; }''',
'''        public required PaperAppRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperAppRuntimeSettingsApi Settings { get; init; }
        public required PaperAppRuntimeStateApi State { get; init; }
        public required PaperAppRuntimePapersApi Papers { get; init; }
        public required PaperAppRuntimeGlobalTopBarApi GlobalTopBar { get; init; }''')
replace(path,
'''            try { Runtime?.Dispose(); } catch { }
            try { GlobalShortcuts.Dispose(); } catch { }
            try { GlobalTopBar.Dispose(); } catch { }
            try { Workspace.Dispose(); } catch { }''',
'''            try { Runtime?.Dispose(); } catch { }
            try { Papers.Dispose(); } catch { }
            try { State.Dispose(); } catch { }
            try { Settings.Dispose(); } catch { }
            try { GlobalShortcuts.Dispose(); } catch { }
            try { GlobalTopBar.Dispose(); } catch { }
            try { Workspace.Dispose(); } catch { }''')
replace(path,
'''        var settings = new PaperAppRuntimeSettingsApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var globalTopBar = new PaperAppRuntimeGlobalTopBarApi(''',
'''        var settings = new PaperAppRuntimeSettingsApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var state = new PaperAppRuntimeStateApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var papers = new PaperAppRuntimePapersApi(
            this,
            descriptor.Id,
            IsActive);
        var globalTopBar = new PaperAppRuntimeGlobalTopBarApi(''')
replace(path,
'''                    Workspace = workspace,
                    Settings = settings,
                    GlobalTopBar = globalTopBar,''',
'''                    Workspace = workspace,
                    Settings = settings,
                    State = state,
                    Papers = papers,
                    GlobalTopBar = globalTopBar,''')
replace(path,
'''                    descriptor,
                    workspace,
                    settings,
                    globalTopBar,''',
'''                    descriptor,
                    workspace,
                    settings,
                    state,
                    papers,
                    globalTopBar,''')
replace(path,
'''                Lifetime = lifetime,
                Workspace = workspace,
                GlobalTopBar = globalTopBar,''',
'''                Lifetime = lifetime,
                Workspace = workspace,
                Settings = settings,
                State = state,
                Papers = papers,
                GlobalTopBar = globalTopBar,''')
replace(path,
'''            try { runtime?.Dispose(); } catch { }
            try { globalShortcuts.Dispose(); } catch { }
            try { globalTopBar.Dispose(); } catch { }
            try { workspace.Dispose(); } catch { }''',
'''            try { runtime?.Dispose(); } catch { }
            try { papers.Dispose(); } catch { }
            try { state.Dispose(); } catch { }
            try { settings.Dispose(); } catch { }
            try { globalShortcuts.Dispose(); } catch { }
            try { globalTopBar.Dispose(); } catch { }
            try { workspace.Dispose(); } catch { }''')
replace(path,
'''        if (statusChanged)
        {
            QueuePluginStatusUiRefresh();
        }''',
'''        foreach (var slot in _pluginAppRuntimeSlots.Values)
        {
            slot.Lease?.Papers.Reconcile();
        }

        if (statusChanged)
        {
            QueuePluginStatusUiRefresh();
        }''')
replace(path,
'''    private void RetryFailedPluginAppRuntimeAfterSettingsChanged(string providerId)
    {''',
'''    private void NotifyPluginAppRuntimeSettingsChanged(string providerId, string settingsJson)
    {
        if (_pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) &&
            slot.State == PluginAppRuntimeState.Running &&
            slot.Lease != null)
        {
            slot.Lease.Settings.PublishChanged(settingsJson);
        }
    }

    private void RetryFailedPluginAppRuntimeAfterSettingsChanged(string providerId)
    {''')

# 5) State API is stateless but participates in lease disposal for symmetry.
path = 'src/PaperAppRuntimeStateApi.cs'
replace(path,
'internal sealed class PaperAppRuntimeStateApi : IPaperPluginRuntimeState\n{',
'internal sealed class PaperAppRuntimeStateApi : IPaperPluginRuntimeState, IDisposable\n{')
replace(path,
'''    private void EnsureActive()
    {
        if (!_isActive())''',
'''    public void Dispose() { }

    private void EnsureActive()
    {
        if (!_isActive()''')

# 6) Controller/frontend route helpers.
path = 'src/AppController.PluginRuntimePapers.cs'
replace(path,
'''    internal bool PostBodyMessageToPluginRuntime(
        string paperId,''',
'''    internal bool CanPostBodyMessageToPluginRuntime(string paperId, string providerId) =>
        _pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State == PluginAppRuntimeState.Running &&
        slot.Lease?.Papers != null &&
        FindPluginRuntimePaper(providerId, paperId) != null;

    internal bool PostBodyMessageToPluginRuntime(
        string paperId,''')

# 7) Every Body context gets a thin Runtime client; Web uses the same route.
path = 'src/PaperWindow.PluginBodies.cs'
replace(path,
'''            Workspace = hostApi,
            SaveStateJson = json => QueuePluginStateSave(''',
'''            Workspace = hostApi,
            Runtime = new PaperPluginRuntimeClient(
                _controller,
                _paper.Id,
                providerId,
                () => _windowLifecycle == PaperWindowLifecycleState.Alive &&
                      !_bodyFailed &&
                      generation == _bodySessionGeneration &&
                      string.Equals(
                          NormalizeBodyProviderId(_paper.BodyProviderId),
                          providerId,
                          StringComparison.Ordinal)),
            SaveStateJson = json => QueuePluginStateSave(''')

# 8) Web Body implements the common Runtime message callback while keeping its existing queue.
path = 'src/WebPaperBodySession.cs'
replace(path,
'''    public FrameworkElement View => _root;

    private WebView2CompositionControl CreateWebView()''',
'''    public FrameworkElement View => _root;

    public bool OnRuntimeMessage(JsonElement message) => ReceiveRuntimeMessage(message);

    private WebView2CompositionControl CreateWebView()''')

print('phase1 transformations complete')
