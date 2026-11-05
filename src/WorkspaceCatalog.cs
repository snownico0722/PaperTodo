using System.IO;

namespace PaperTodo;

/// <summary>
/// Resolves numbered workspace storage and the main-owned catalog. Display names never rename
/// the on-disk documents, and inactive workspaces have no live presentation or plugin session.
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

    // Purge inactive secondary data after removing the slot from the main catalog.
    // A failed file cleanup is reported but does not restore catalog membership.
    internal void DeleteInactiveSlotFiles(int number)
    {
        if (number is < 2 or > 5 || Options.Active == number)
            throw new InvalidOperationException("Only inactive secondary workspaces may be deleted.");
        var paths = PathsFor(number);
        var stem = "data" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(stem + ".json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(stem + ".backup.json", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(stem + "conflict-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(stem + ".failed_load.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(stem + ".recovered", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
        File.Delete(paths.Images);
        File.Delete(paths.Images + "-lock");
        if (System.IO.Directory.Exists(paths.PluginData))
            System.IO.Directory.Delete(paths.PluginData, recursive: true);
    }

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

    internal WorkspaceSession Open(int number, WorkspaceConfiguration options, AppState? main = null)
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
        // Normalize papers against effective settings, not dormant local preferences.
        store.InheritedSettings = inherited;
        var state = store.Load(out var initializedEmpty);
        if (initializedEmpty && number > 1)
        {
            // New or irrecoverably corrupt slots both start with one blank paper.
            // Image, plugin and historical files never veto initialization.
            // Every new slot begins with main-workspace settings. Unfollowing later
            // keeps this initial copy as its independent preference, regardless of
            // which workspace was active when the slot was first opened.
            main ??= MainStore.Load();
            inherited ??= WorkspaceSettingsSnapshot.Capture(main);
            inherited.ApplyTo(state);
            state.Papers.Add(new PaperData());
        }
        if (number == 1)
        {
            state.Workspaces = HasConfiguration ? options : null;
            var mainSession = new WorkspaceSession(paths, store, state, false, null);
            return mainSession;
        }

        state.Workspaces = null;
        var independent = WorkspaceSettingsSnapshot.Capture(state);
        if (follows) inherited!.ApplyTo(state);
        var session = new WorkspaceSession(paths, store, state, follows, independent);
        return session;
    }

    // Settings inheritance does not load or duplicate the current document. A toggle
    // only exchanges snapshots; the existing papers, windows and write owner stay live.
    internal (WorkspaceSession Session, WorkspaceSettingsSnapshot EffectiveSettings) PreviewSettingsChange(
        WorkspaceSession current, WorkspaceConfiguration options)
    {
        var number = current.Paths.Number;
        var follows = options.FollowsMain(number);
        if (number == 1 || follows == current.FollowsMain)
            throw new InvalidOperationException("Only a numbered workspace can change its settings inheritance.");

        var independent = current.FollowsMain
            ? current.IndependentSettings!
            : WorkspaceSettingsSnapshot.Capture(current.State);
        var inherited = follows ? WorkspaceSettingsSnapshot.Capture(MainStore.Load()) : null;
        var session = new WorkspaceSession(current.Paths, current.Store, current.State, follows, independent)
        {
            InheritedSettings = inherited
        };
        return (session, inherited ?? independent);
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
