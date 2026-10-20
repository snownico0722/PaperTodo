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
        if (Options.Enabled && Options.Active > 1)
        {
            try { return Open(Options.Active, Options, allowCreate: false, main); }
            catch (Exception ex)
            {
                // A missing/broken secondary never manufactures an empty replacement at startup.
                // Keep its files intact and make the main workspace usable, with an explicit notice.
                StartupError = ex.GetBaseException().Message;
                Options.Active = 1;
            }
        }
        main.Workspaces = HasConfiguration ? Options : null;
        return new(PathsFor(1), MainStore, main, false, null);
    }

    internal WorkspaceSession Open(int number, WorkspaceConfiguration options, bool allowCreate,
        AppState? main = null)
    {
        var paths = PathsFor(number);
        var store = StoreFor(number);
        var exists = File.Exists(paths.Data) || File.Exists(paths.Backup);
        if (!exists && (!allowCreate || number == 1))
            throw new FileNotFoundException("Workspace data is missing.", paths.Data);
        if (!exists && number > 1 &&
            (File.Exists(paths.Images) || System.IO.Directory.Exists(paths.PluginData)))
            throw new InvalidDataException("Workspace data is missing but its images or plugin data still exist: " + paths.Data);

        var follows = options.FollowsMain(number);
        WorkspaceSettingsSnapshot? inherited = null;
        if (number > 1 && (follows || !exists))
        {
            main ??= MainStore.Load();
            inherited = WorkspaceSettingsSnapshot.Capture(main);
        }
        // Normalize paper titles/collapse state using the EFFECTIVE settings, not a dormant
        // independent preference (otherwise entering an inherited workspace can truncate titles).
        store.InheritedSettings = follows ? inherited : null;
        var state = store.Load();
        if (!exists && number > 1)
        {
            // First entry starts with the main software settings and one ordinary blank paper,
            // not copies of the main workspace's notes, reminders or desktop geometry.
            inherited!.ApplyTo(state);
            state.Papers.Add(new PaperData());
        }
        if (number == 1)
        {
            state.Workspaces = HasConfiguration ? options : null;
            return new(paths, store, state, false, null);
        }

        state.Workspaces = null;
        var independent = WorkspaceSettingsSnapshot.Capture(state);
        if (follows) inherited!.ApplyTo(state);
        return new(paths, store, state, follows, independent);
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
