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
    private readonly Func<StateStore, DateTime?, WorkspaceRecoveryChoice>? _confirmRecovery;
    private readonly Func<StateStore, Exception, WorkspaceRecoveryChoice>? _confirmBackupReadFailure;
    private AppState? _initialMain;

    internal WorkspaceCatalog(string directory, IDurableAtomicFileWriter writer,
        Func<StateStore, DateTime?, WorkspaceRecoveryChoice>? confirmRecovery = null,
        Func<StateStore, Exception, WorkspaceRecoveryChoice>? confirmBackupReadFailure = null)
    {
        _directory = Path.GetFullPath(directory);
        _writer = writer;
        _confirmRecovery = confirmRecovery;
        _confirmBackupReadFailure = confirmBackupReadFailure;
        _initialMain = LoadWorkspace(StoreFor(1), out _);
        HasConfiguration = _initialMain.Workspaces != null;
        Options = (_initialMain.Workspaces ?? new()).Normalized();
    }

    internal WorkspaceConfiguration Options { get; private set; }
    internal bool HasConfiguration { get; private set; }
    internal string? StartupError { get; private set; }
    internal StateStore MainStore => StoreFor(1);
    internal WorkspacePaths PathsFor(int number) => new(_directory, number);

    private AppState LoadWorkspace(StateStore store, out bool initializedEmpty) =>
        store.Load(out initializedEmpty, _confirmRecovery == null ? null :
            timestamp => _confirmRecovery(store, timestamp), _confirmBackupReadFailure == null ? null :
            error => _confirmBackupReadFailure(store, error));

    private AppState ReadMainForSettings()
    {
        try { return MainStore.ReadCommittedWorkspaceState(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new StateRecoveryException("WorkspaceFailureMainRecoveryRequired");
        }
    }

    // Purge inactive secondary data after removing the slot from the main catalog.
    // A failed file cleanup is reported but does not restore catalog membership.
    internal void DeleteInactiveSlotFiles(int number)
    {
        if (number is < 2 or > 5 || Options.Active == number)
            throw new InvalidOperationException("Only inactive secondary workspaces may be deleted.");
        var paths = PathsFor(number);
        var stem = "data" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var primary = stem + ".json";
        var backup = stem + ".backup.json";
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            // Include atomic-write temps and copies created by StateStore recovery.
            // Match the complete numbered stem: deleting data2 must not touch data20.
            if (name.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(primary + ".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(backup, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(backup + ".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(stem + "conflict-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(stem + ".failed_load.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(stem + ".backup.used_for_recovery.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(primary + ".orphaned.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(backup + ".orphaned.", StringComparison.OrdinalIgnoreCase) ||
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
            catch (WorkspaceRecoveryFallbackException)
            {
                // Explicit fallback uses the already loaded, committed main workspace.
                Options.Active = 1;
                SaveOptions(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An inaccessible slot can be retried later; do not reset the
                // user's selected workspace or write over its unavailable data.
                StartupError = WorkspaceFailureMessage.For(ex);
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
            main ??= ReadMainForSettings();
            inherited = WorkspaceSettingsSnapshot.Capture(main);
        }
        // Normalize papers against effective settings, not dormant local preferences.
        store.InheritedSettings = inherited;
        var state = LoadWorkspace(store, out var initializedEmpty);
        if (initializedEmpty && number > 1)
        {
            // New slots and explicitly restarted slots begin with one blank paper.
            // Image, plugin and historical files never veto initialization.
            // Every new slot begins with main-workspace settings. Unfollowing later
            // keeps this initial copy as its independent preference, regardless of
            // which workspace was active when the slot was first opened.
            main ??= ReadMainForSettings();
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
        var inherited = follows ? WorkspaceSettingsSnapshot.Capture(ReadMainForSettings()) : null;
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
