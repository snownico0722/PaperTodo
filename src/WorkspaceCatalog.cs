using System.IO;

namespace PaperTodo;

/// <summary>
/// Resolves fixed slots and their immutable stores. No watcher, directory discovery, rename,
/// mirrored catalog or inactive-workspace runtime is created.
/// </summary>
internal sealed class WorkspaceCatalog
{
    private readonly string _directory;
    private readonly IDurableAtomicFileWriter _writer;
    private readonly Dictionary<int, StateStore> _stores = new();
    private readonly PersistenceWriteLifetime _writeLifetime = new();
    private AppState? _initialMain;

    internal WorkspaceCatalog(string directory, IDurableAtomicFileWriter writer)
    {
        _directory = Path.GetFullPath(directory);
        _writer = writer;
        _initialMain = StoreFor(1).Load();
        HasConfiguration = _initialMain.Workspaces != null;
        Options = (_initialMain.Workspaces ?? new()).Normalized();
    }

    internal WorkspaceConfiguration Options { get; private set; }
    internal bool HasConfiguration { get; private set; }
    internal string? StartupError { get; private set; }
    internal void SetStartupError(string message) => StartupError = message;
    internal StateStore MainStore => StoreFor(1);
    internal WorkspacePaths PathsFor(int number) => new(_directory, number);

    internal StateStore StoreFor(int number)
    {
        if (!_stores.TryGetValue(number, out var store))
        {
            _ = PathsFor(number); // validate before indexing or touching a file
            _stores[number] = store = new StateStore(_directory, _writer, number) { WriteLifetime = _writeLifetime };
        }
        return store;
    }

    internal WorkspaceSession OpenInitial()
    {
        var main = _initialMain ?? throw new InvalidOperationException("The initial workspace was already opened.");
        _initialMain = null;
        // The main document owns the workspace catalog even while a numbered
        // workspace is active. Finish its backup recovery before relying on it
        // for subsequent switches or catalog updates.
        if (MainStore.NeedsStartupRecoverySave)
            MainStore.SaveJsonSync(MainStore.SerializeState(main), 1);
        if (Options.Enabled && Options.Active > 1)
        {
            try { return Open(Options.Active, Options, main); }
            catch (Exception ex)
            {
                // An inaccessible slot can be retried later; do not reset the
                // user's selected workspace or write over its unavailable data.
                StartupError = ex.GetBaseException().Message;
            }
        }
        main.Workspaces = HasConfiguration ? Options : null;
        return new(PathsFor(1), MainStore, main, false, null);
    }

    internal WorkspaceSession Open(int number, WorkspaceConfiguration options,
        AppState? main = null, WorkspaceSession? current = null)
    {
        var paths = PathsFor(number);
        var store = StoreFor(number);
        var follows = options.FollowsMain(number);
        WorkspaceSettingsSnapshot? inherited = null;
        if (number > 1 && follows)
        {
            main ??= MainStore.Load();
            inherited = WorkspaceSettingsSnapshot.Capture(main);
        }
        // Normalize paper titles/collapse state using the EFFECTIVE settings, not a dormant
        // independent preference (otherwise entering an inherited workspace can truncate titles).
        // A same-slot settings handoff must not Load() the live store: Load accepts
        // new disk bytes and could authorize writes from an older in-memory state on rollback.
        AppState state;
        var initializedEmpty = false;
        if (current != null)
        {
            if (current.Paths.Number != number || !ReferenceEquals(current.Store, store))
                throw new InvalidOperationException("The preview session belongs to another workspace.");
            state = StateStore.CopyForReload(current.State);
            if (current.FollowsMain) current.IndependentSettings!.ApplyTo(state);
        }
        else
        {
            store.InheritedSettings = follows ? inherited : null;
            state = store.Load(out initializedEmpty);
        }
        if (initializedEmpty && number > 1)
        {
            // New or irrecoverably corrupt slots both start with one blank paper.
            // Image, plugin and historical files never veto initialization.
            // Independent new slots use their own/default preferences. A caller with
            // an already-loaded main state may provide defaults without another disk read.
            inherited ??= main is null ? null : WorkspaceSettingsSnapshot.Capture(main);
            inherited?.ApplyTo(state);
            state.Papers.Add(new PaperData());
        }
        if (number == 1)
        {
            state.Workspaces = HasConfiguration ? options : null;
            var mainSession = new WorkspaceSession(paths, store, state, false, null);
            if (current != null) mainSession.InheritedSettings = null;
            return mainSession;
        }

        state.Workspaces = null;
        var independent = WorkspaceSettingsSnapshot.Capture(state);
        if (follows) inherited!.ApplyTo(state);
        var session = new WorkspaceSession(paths, store, state, follows, independent);
        if (current != null) session.InheritedSettings = follows ? inherited : null;
        return session;
    }

    internal void AdoptOptions(WorkspaceConfiguration options, bool hasConfiguration = true)
    {
        Options = options.Normalized();
        HasConfiguration = hasConfiguration;
    }

    internal void SaveOptions(long version) => MainStore.UpdateWorkspaceConfiguration(Options, version);

    internal AppState AcceptReload(WorkspaceSession session, AppState candidate)
    {
        if (session.Paths.Number == 1)
        {
            var options = (candidate.Workspaces ?? new()).Normalized();
            // reload_data reloads this workspace; a stored startup slot is not a switch command.
            options.Active = 1;
            AdoptOptions(options, candidate.Workspaces != null);
            candidate.Workspaces = HasConfiguration ? Options : null;
        }
        else
        {
            candidate.Workspaces = null;
            session.IndependentSettings = WorkspaceSettingsSnapshot.Capture(candidate);
            if (session.FollowsMain) session.InheritedSettings!.ApplyTo(candidate);
            session.BindPersistence();
        }
        return candidate;
    }
}
