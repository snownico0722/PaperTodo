using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void WorkspaceStorageChecks()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("explicit state field ownership and fail-closed inheritance", WorkspaceFieldScopeChecks),
            ("legacy main and numbered paths", WorkspacePathsAndLegacy),
            ("main catalog updates preserve content and fence old writers", WorkspaceMetadataWrites),
            ("main backup recovery persists before secondary activation", WorkspaceMainRecoveryBeforeSecondaryStart),
            ("follow-main overlay preserves dormant preferences and layout", WorkspaceInheritance),
            ("settings reads never stage a main-workspace recovery", WorkspaceSettingsReadDoesNotRecover),
            ("image recovery protection is per workspace", WorkspaceImageProtection),
            ("plugin detach flush is strict and recoverable", WorkspacePluginStores),
            ("bad startup workspace falls back without replacing it", WorkspaceStartupFallback),
            ("bad candidate cannot switch or mutate the active in-memory state", WorkspacePreviewDoesNotAcceptSource),
            ("inactive following state is released but reload updates its settings projection", WorkspaceSessionRelease)
        };
        foreach (var check in checks)
        {
            check.Run();
            Console.WriteLine("PASS workspace: " + check.Name);
        }
    }


    private static void WorkspaceFieldScopeChecks()
    {
        var actual = typeof(AppState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod != null && p.SetMethod?.IsPublic == true)
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var roles = WorkspaceFieldScopes.Declared;
        var missing = actual.Except(roles.Keys, StringComparer.Ordinal).OrderBy(x => x).ToArray();
        var stale = roles.Keys.Except(actual, StringComparer.Ordinal).OrderBy(x => x).ToArray();
        Require(missing.Length == 0 && stale.Length == 0,
            "Every writable AppState field needs an explicit workspace scope. Missing: " +
            string.Join(", ", missing) + "; stale: " + string.Join(", ", stale));
        Require(!WorkspaceFieldScopes.Inherits("FutureUnclassifiedField"),
            "New state fields must default to local, not silently inherit main settings.");
        Require(roles[nameof(AppState.Workspaces)] == WorkspaceStateFieldScope.MainCatalog &&
                roles[nameof(AppState.Papers)] == WorkspaceStateFieldScope.LocalContent &&
                roles[nameof(AppState.DeepCapsuleSide)] == WorkspaceStateFieldScope.LocalLayout &&
                roles[nameof(AppState.DeepCapsuleQueueStartTopMargins)] == WorkspaceStateFieldScope.LocalLayout,
            "Main catalog, paper content and window geometry must be separately owned.");
        Require(roles[nameof(AppState.TodoTopBarShortcuts)] == WorkspaceStateFieldScope.LegacyMigration &&
                roles[nameof(AppState.NoteTopBarShortcuts)] == WorkspaceStateFieldScope.LegacyMigration,
            "Top-bar migration inputs need an explicit legacy classification.");
        Require(WorkspaceFieldScopes.Inherits(nameof(AppState.DataMergeConflictPolicy)) &&
                WorkspaceFieldScopes.Inherits(nameof(AppState.McpAllowFullWrites)) &&
                WorkspaceFieldScopes.Inherits(nameof(AppState.McpAllowDeletes)) &&
                WorkspaceFieldScopes.Inherits(nameof(AppState.GlobalHotkeys)) &&
                WorkspaceFieldScopes.Inherits(nameof(AppState.DisabledPluginIds)),
            "Existing security-sensitive settings-follow behavior must remain explicit.");

        var main = new AppState
        {
            Theme = "dark",
            McpAllowFullWrites = true,
            DataMergeConflictPolicy = DataMergeConflictPolicies.External,
            Papers = [new PaperData { Title = "Main only" }]
        };
        main.GlobalHotkeys["test"] = "Ctrl+Shift+K";
        main.DisabledPluginIds.Add("disabled-on-main");
        main.DeepCapsuleQueueStartTopMargins["main|right"] = 30;
        var secondary = new AppState
        {
            Theme = "light",
            McpAllowFullWrites = false,
            Papers = [new PaperData { Title = "Secondary only" }],
            DeepCapsuleSide = DeepCapsuleSides.Left,
            Workspaces = new WorkspaceConfiguration { Enabled = true }
        };
        secondary.DeepCapsuleQueueStartTopMargins["secondary|left"] = 90;
        var snapshot = WorkspaceSettingsSnapshot.Capture(main);
        snapshot.ApplyTo(secondary);
        Require(secondary.Theme == "dark" && secondary.McpAllowFullWrites &&
                secondary.DataMergeConflictPolicy == DataMergeConflictPolicies.External &&
                secondary.GlobalHotkeys["test"] == "Ctrl+Shift+K" &&
                secondary.DisabledPluginIds.SequenceEqual(["disabled-on-main"]),
            "Declared software settings did not inherit as before.");
        Require(secondary.Papers.Single().Title == "Secondary only" &&
                secondary.DeepCapsuleSide == DeepCapsuleSides.Left &&
                secondary.DeepCapsuleQueueStartTopMargins.Count == 1 &&
                secondary.DeepCapsuleQueueStartTopMargins.ContainsKey("secondary|left") &&
                secondary.Workspaces?.Enabled == true,
            "Independent paper, geometry or catalog leaked across workspace boundaries.");
        secondary.GlobalHotkeys["test"] = "Ctrl+Alt+L";
        secondary.DisabledPluginIds.Clear();
        Require(main.GlobalHotkeys["test"] == "Ctrl+Shift+K" &&
                main.DisabledPluginIds.SequenceEqual(["disabled-on-main"]),
            "Settings snapshot must not share mutable collections with the main AppState.");
    }

    private static void WorkspacePathsAndLegacy()
    {
        using var scope = new Scope();
        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var main = catalog.OpenInitial();
        main.BindPersistence();
        Require(main.Paths.Number == 1 && !catalog.Options.Enabled && !main.FollowsMain,
            "legacy workspace changed behavior");
        Require(!J(main.Store.SerializeState(main.State)).ContainsKey("workspaces"),
            "disabled legacy save added a catalog");
        for (var n = 1; n <= 5; n++)
        {
            var suffix = n == 1 ? "" : n.ToString(CultureInfo.InvariantCulture);
            var paths = catalog.PathsFor(n);
            Require(Path.GetFileName(paths.Data) == $"data{suffix}.json" &&
                Path.GetFileName(paths.Images) == $"note-assets{suffix}.lmdb" &&
                Path.GetFileName(paths.Backup) == $"data{suffix}.backup.json" &&
                Path.GetFileName(paths.PluginData) == $"data{suffix}", "wrong suffix placement");
            Require(ReferenceEquals(catalog.StoreFor(n), catalog.StoreFor(n)),
                "revisiting a slot created an independent writer lock/version");
        }
        Throws<ArgumentOutOfRangeException>(() => catalog.PathsFor(6));
        var options = new WorkspaceConfiguration
        {
            Enabled = true, Slots = [0, 1, 2, 3, 4, 5, 6], Active = 99
        }.Normalized();
        Require(options.SlotNumbers.SequenceEqual([1, 2, 3, 4, 5]) &&
                options.Active == 1 && options.FollowsMain(2) && !options.FollowsMain(1),
            "invalid slot IDs or follow default");
        Require(!File.Exists(Path.Combine(scope.DirectoryPath, "workspaces.json")), "unexpected separate catalog");
        var managed = new WorkspaceConfiguration
        {
            Enabled = true, Active = 4,
            Slots = [1, 3, 4], Names = new() { [3] = "开发" },
            TopBarNamePicker = true
        }.Normalized();
        Require(managed.SlotNumbers.SequenceEqual([1, 3, 4]) &&
                !managed.ContainsSlot(2) && managed.NextSlot(1) == 3 &&
                managed.NextSlot(4) == 1 && managed.DisplayName(3) == "开发" &&
                managed.DisplayName(1) == Strings.Format("WorkspaceSlotLabel", 1) &&
                managed.TopBarNamePicker,
            "noncontiguous named workspace catalog lost its membership or display mode");
        var secondFiles = catalog.PathsFor(2);
        var deletedArtifacts = new[]
        {
            secondFiles.Data, secondFiles.Data + ".tmp",
            secondFiles.Backup, secondFiles.Backup + ".tmp",
            Path.Combine(scope.DirectoryPath, "data2conflict-sample.json"),
            Path.Combine(scope.DirectoryPath, "data2conflict-sample.diff.json.tmp"),
            Path.Combine(scope.DirectoryPath, "data2.failed_load.20260101.json"),
            Path.Combine(scope.DirectoryPath, "data2.backup.used_for_recovery.20260101.json"),
            Path.Combine(scope.DirectoryPath, "data2.json.orphaned.20260101.tmp"),
            Path.Combine(scope.DirectoryPath, "data2.backup.json.orphaned.20260101.tmp"),
            Path.Combine(scope.DirectoryPath, "data2.recovered.20260101.json")
        };
        foreach (var artifact in deletedArtifacts)
            File.WriteAllText(artifact, "to be deleted");
        Directory.CreateDirectory(secondFiles.PluginData);
        File.WriteAllText(Path.Combine(secondFiles.PluginData, "settings.json"), "plugin state");
        File.WriteAllText(secondFiles.Images, "image data");
        File.WriteAllText(secondFiles.Images + "-lock", "image lock");

        // Another workspace and a similarly prefixed filename must remain untouched.
        var otherWorkspaceArtifact = Path.Combine(scope.DirectoryPath, "data3.json.tmp");
        var similarlyNamedArtifact = Path.Combine(scope.DirectoryPath, "data20.json.tmp");
        File.WriteAllText(otherWorkspaceArtifact, "keep workspace 3");
        File.WriteAllText(similarlyNamedArtifact, "keep unrelated file");

        catalog.AdoptOptions(new WorkspaceConfiguration
        {
            Enabled = true, Slots = [1, 2]
        });
        var removed = catalog.Options.Normalized();
        removed.Slots = [1];
        catalog.AdoptOptions(removed);
        Require(!catalog.Options.ContainsSlot(2),
            "removed workspace still appears in the catalog");
        catalog.DeleteInactiveSlotFiles(2);
        Require(deletedArtifacts.All(file => !File.Exists(file)) &&
                !File.Exists(secondFiles.Images) && !File.Exists(secondFiles.Images + "-lock") &&
                !Directory.Exists(secondFiles.PluginData) &&
                File.Exists(catalog.PathsFor(1).Data) &&
                File.ReadAllText(otherWorkspaceArtifact) == "keep workspace 3" &&
                File.ReadAllText(similarlyNamedArtifact) == "keep unrelated file",
            "deleting a workspace left storage artifacts or affected other workspaces");
    }

    private static void WorkspaceMetadataWrites()
    {
        using var scope = new Scope();
        var source = scope.Read();
        source["futureExtension"] = J("{\"keep\":123}");
        scope.Write(source);
        var options = new WorkspaceConfiguration { Enabled = true, Slots = [1, 2, 3, 4, 5], Active = 3 };
        scope.Store.UpdateWorkspaceConfiguration(options, 5);
        scope.Store.SaveJsonAsync(scope.BaselineJson, 4).GetAwaiter().GetResult();
        var saved = scope.Read();
        Require(JsonNode.DeepEquals(saved["papers"], source["papers"]) &&
            JsonNode.DeepEquals(saved["futureExtension"], source["futureExtension"]),
            "catalog update overwrote main papers/unknown properties");
        Require(saved["workspaces"]!["active"]!.GetValue<int>() == 3, "old async save undid catalog");
        Throws<InvalidOperationException>(() =>
            new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2)
                .UpdateWorkspaceConfiguration(options, 6));
        var bytes = File.ReadAllBytes(scope.Store.FilePath);
        var failed = new StateStore(scope.DirectoryPath, new DurableAtomicFileWriter((stage, _) =>
        {
            if (stage == DurableAtomicWriteStage.BeforeReplace) throw new IOException("injected catalog failure");
        }));
        Throws<IOException>(() => failed.UpdateWorkspaceConfiguration(
            new WorkspaceConfiguration { Enabled = true, Slots = [1, 2], Active = 2 }, 7));
        Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(bytes), "failed catalog write replaced main");
    }

    private static void WorkspaceMainRecoveryBeforeSecondaryStart()
    {
        using var scope = new Scope();
        var main = scope.Store.Load();
        main.Workspaces = new() { Enabled = true, Slots = [1, 2, 3], Active = 2 };
        main.Papers[0].Content = "main content must survive recovery";
        scope.Store.SaveJsonSync(scope.Store.SerializeState(main), 2);
        Require(scope.Store.TryRefreshBackupFromPrimary(), "main recovery test needs a valid backup");
        var secondary = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        var secondState = NewState();
        secondState.Papers[0].Content = "secondary independent content";
        secondary.SaveJsonSync(secondary.SerializeState(secondState), 1);
        var originalSecondary = File.ReadAllBytes(secondary.FilePath);
        File.WriteAllText(scope.Store.FilePath, "{corrupt main");

        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var active = catalog.OpenInitial();
        Require(active.Paths.Number == 2 && catalog.StartupError == null &&
                !catalog.MainStore.NeedsStartupRecoverySave,
            "starting a numbered workspace left the main workspace recovery pending");
        var recoveredMain = J(File.ReadAllText(catalog.MainStore.FilePath));
        Require(recoveredMain["workspaces"]!["active"]!.GetValue<int>() == 2 &&
                Str(recoveredMain["papers"]![0]!["content"]) == "main content must survive recovery" &&
                Directory.GetFiles(scope.DirectoryPath, "data.failed_load.*.json").Length == 0 &&
                Directory.GetFiles(scope.DirectoryPath, "data.backup.used_for_recovery.*.json").Length == 0 &&
                File.ReadAllBytes(secondary.FilePath).SequenceEqual(originalSecondary),
            "main recovery lost the selected slot, saved content, or added redundant copies");

        var next = catalog.Options.Normalized();
        next.Active = 3;
        catalog.AdoptOptions(next);
        catalog.SaveOptions(3);
        Require(J(File.ReadAllText(catalog.MainStore.FilePath))["workspaces"]!["active"]!.GetValue<int>() == 3,
            "restored main catalog could not accept a subsequent workspace switch");
    }

    private static void WorkspaceSettingsReadDoesNotRecover()
    {
        foreach (var missing in new[] { false, true })
        {
            using var scope = new Scope();
            var state = NewState();
            state.Workspaces = new() { Enabled = true, Slots = [1, 2, 3], FollowMainSettings = new() { [2] = false } };
            scope.Store.SaveJsonSync(scope.Store.SerializeState(state), 2);
            var prompts = 0;
            var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared, (_, _) =>
            {
                prompts++;
                return WorkspaceRecoveryChoice.Restore;
            });
            catalog.OpenInitial();
            var current = catalog.Open(2, catalog.Options);
            var next = catalog.Options.Normalized();
            next.FollowMainSettings[2] = true;
            WriteHistoryFixture(scope.DirectoryPath, DateTime.UtcNow, ("data.json", scope.Store.SerializeState(state)));
            File.WriteAllText(scope.Store.BackupPath, "{broken backup");
            if (missing) File.Delete(scope.Store.FilePath);
            else File.WriteAllText(scope.Store.FilePath, "{broken main");

            Throws<StateRecoveryException>(() => catalog.PreviewSettingsChange(current, next));
            Throws<StateRecoveryException>(() => catalog.Open(3, next));
            Require(prompts == 0 && !catalog.MainStore.NeedsStartupRecoverySave &&
                    !current.FollowsMain && (missing ? !File.Exists(scope.Store.FilePath) :
                        File.ReadAllText(scope.Store.FilePath) == "{broken main"),
                "settings-only read prompted, staged recovery or replaced main");

            var recovered = catalog.Open(1, catalog.Options);
            Require(prompts == 1 && recovered.Store.NeedsStartupRecoverySave,
                "actual main entry failed to offer historical recovery");
            recovered.Store.SaveJsonSync(recovered.Store.SerializeState(recovered.State), 3);
            var preview = catalog.PreviewSettingsChange(current, next);
            Require(preview.Session.FollowsMain && prompts == 1 && !catalog.MainStore.NeedsStartupRecoverySave,
                "settings inheritance did not resume after main recovery committed");
        }
    }

    private static void WorkspaceInheritance()
    {
        using var scope = new Scope();
        var main = scope.Store.Load();
        main.Theme = "dark";
        main.MaxTitleLength = 20;
        main.UseCapsuleMode = main.UseDeepCapsuleMode = true;
        main.McpEnabled = main.McpAllowFullWrites = true;
        main.Workspaces = new() { Enabled = true, Slots = [1, 2, 3] };
        main.DeepCapsuleQueueStartTopMargins[@"\\.\DISPLAY1|left"] = 55;
        scope.Store.SaveJsonSync(scope.Store.SerializeState(main), 2);
        var secondaryStore = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        var own = NewState();
        own.Theme = "light";
        own.MaxTitleLength = 2;
        own.UseCapsuleMode = own.UseDeepCapsuleMode = false;
        own.McpEnabled = own.McpAllowFullWrites = false;
        own.DeepCapsuleQueueStartTopMargins[@"\\.\DISPLAY1|right"] = 177;
        own.Papers[0].Title = "long title";
        own.Papers[0].IsCollapsed = true;
        secondaryStore.SaveJsonSync(secondaryStore.SerializeState(own), 1);
        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        _ = catalog.OpenInitial();
        var session = catalog.Open(2, catalog.Options);
        session.BindPersistence();
        Require(session.State.Theme == "dark" && session.State.McpEnabled && session.State.McpAllowFullWrites,
            "main software settings were not inherited");
        Require(session.State.Papers[0].Title == "long title" && session.State.Papers[0].IsCollapsed,
            "dormant settings normalized/truncated content before inheritance");
        Require(!session.State.DeepCapsuleQueueStartTopMargins.Values.Contains(55) &&
            session.State.DeepCapsuleQueueStartTopMargins.Values.Contains(177), "main layout leaked into secondary");
        session.State.Papers[0].Content = "secondary edit";
        var projected = J(session.Store.SerializeState(session.State));
        Require(Str(projected["theme"]) == "light" && !projected["mcpEnabled"]!.GetValue<bool>(),
            "saving inherited state overwrote dormant preferences");
        Require(Str(projected["papers"]![0]!["content"]) == "secondary edit" && !projected.ContainsKey("workspaces"),
            "projection lost content or wrote a second catalog");
        session.Store.SaveJsonSync(projected.ToJsonString(), 4);
        var independent = catalog.Options.Normalized(); independent.FollowMainSettings[2] = false;
        var (preview, effectiveSettings) = catalog.PreviewSettingsChange(session, independent);
        var effective = new AppState();
        effectiveSettings.ApplyTo(effective);
        Require(ReferenceEquals(preview.State, session.State) && !preview.FollowsMain &&
                effective.Theme == "light" && !effective.McpEnabled,
            "changing inheritance duplicated the live workspace or lost its independent preferences");
        var next = catalog.Open(2, independent);
        next.BindPersistence();
        Require(next.State.Theme == "light" && !next.State.McpEnabled && !next.FollowsMain,
            "unfollow did not restore independent settings");
        Require(next.State.MaxTitleLength == 2 && next.State.Papers[0].Title == "long title",
            "reopening with a smaller independent display limit truncated the persisted title");
        var newSlot = catalog.Open(3, catalog.Options);
        Require(newSlot.State.Papers.Count == 1 && newSlot.State.Papers[0].Content.Length == 0 &&
            newSlot.State.Papers[0].Id != main.Papers[0].Id, "new workspace copied main content");
        // New slots always start with main settings, even when they are
        // immediately independent and are entered from another secondary slot.
        var independentNew = catalog.Options.Normalized();
        independentNew.FollowMainSettings[3] = false;
        var fromMain = catalog.Open(3, independentNew, main);
        var fromSecondary = catalog.Open(3, independentNew);
        Require(!fromMain.FollowsMain && !fromSecondary.FollowsMain &&
                fromMain.State.Theme == "dark" && fromSecondary.State.Theme == "dark" &&
                fromMain.State.MaxTitleLength == 20 && fromSecondary.State.MaxTitleLength == 20 &&
                fromMain.State.McpEnabled && fromSecondary.State.McpEnabled,
            "independent new slot did not use consistent main-workspace initial settings");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BindRetiredWorkspace(WorkspaceCatalog catalog)
    {
        var session = catalog.Open(2, catalog.Options);
        session.BindPersistence();
        return new WeakReference(session.State);
    }

    private static void WorkspaceSessionRelease()
    {
        using var scope = new Scope();
        var secondary = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        secondary.SaveJsonSync(secondary.SerializeState(NewState()), 1);
        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        _ = catalog.OpenInitial();
        var retired = BindRetiredWorkspace(catalog);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!retired.IsAlive, "cached writer retained the entire inactive workspace state");
        var session = catalog.Open(2, catalog.Options);
        session.BindPersistence();
        var incoming = NewState(); incoming.Theme = "light";
        var effective = catalog.AcceptReload(session, incoming);
        StateReloadModels.Apply(session.State, effective);
        Require(Str(J(session.Store.SerializeState(session.State))["theme"]) == "light",
            "reload kept a stale independent-settings projection");
        GC.KeepAlive(catalog);
    }

    private static void WorkspaceFollowingMergeBaseline()
    {
        // A following workspace uses the main title limit, not its dormant
        // independent preference. Both merge inputs must use the same rules.
        using var scope = new Scope();
        var main = scope.Store.Load();
        main.MaxTitleLength = 20;
        scope.Store.SaveJsonSync(scope.Store.SerializeState(main), 2);
        var secondary = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        var own = NewState();
        own.MaxTitleLength = 6;
        own.Papers[0].Title = "ABCDEFGHIJ";
        secondary.SaveJsonSync(secondary.SerializeState(own), 1);
        var baselineJson = File.ReadAllText(secondary.FilePath);

        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        _ = catalog.OpenInitial();
        var session = catalog.Open(2, catalog.Options);
        session.BindPersistence();
        Require(session.FollowsMain && session.State.MaxTitleLength == 20 &&
                session.State.Papers[0].Title == "ABCDEFGHIJ",
            "Fixture did not retain the full title under inherited settings.");

        var external = J(baselineJson);
        external["papers"]![0]!["title"] = "KLMNOPQRST";
        File.WriteAllText(secondary.FilePath, external.ToJsonString());
        var preview = session.Store.PreviewExternalMerge(session.State, 2, baselineJson);
        Require(preview.State != null && preview.Conflicts is { Count: 0 } &&
                preview.State.Papers[0].Title == "KLMNOPQRST",
            "Dormant title limit created a fake conflict or rejected an external-only edit.");
        Require(Str(J(File.ReadAllText(secondary.FilePath))["papers"]![0]!["title"]) == "KLMNOPQRST",
            "The merge preview unexpectedly rewrote the external document.");
        Console.WriteLine("PASS following workspace uses effective title settings for merge baseline.");
    }

    private static void WorkspaceImageProtection()
    {
        using var scope = new Scope();
        var first = NewState(); first.Papers[0].Content = "![main](i:101)";
        scope.Store.SaveJsonSync(scope.Store.SerializeState(first), 2);
        var second = NewState(); second.Papers[0].Content = "![second](i:202)";
        var store2 = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        store2.SaveJsonSync(store2.SerializeState(second), 1);
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "dataconflict-a.json"),
            scope.Store.SerializeState(first));
        var archive2 = NewState(); archive2.Papers[0].Content = "![archived](i:303)";
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "data2conflict-a.json"),
            store2.SerializeState(archive2));
        Require(scope.Store.TryCollectProtectedImageIds(first, out var mainIds) &&
            mainIds.Contains("101") && !mainIds.Contains("303"), "main scanned secondary image archives");
        Require(store2.TryCollectProtectedImageIds(second, out var secondaryIds) &&
            secondaryIds.Contains("303") && !secondaryIds.Contains("101"), "secondary scanned main archives");
        Require(store2.TryRefreshBackupFromPrimary() && File.Exists(Path.Combine(scope.DirectoryPath, "data2.backup.json")),
            "secondary backup used main filename");

        var missingImagePath = Path.Combine(scope.DirectoryPath, "note-assets-missing.lmdb");
        using var missingImages = new NoteImageStore(missingImagePath);
        missingImages.Load();
        missingImages.ProtectPersistedImageReferences(second, secondaryIds, hasPersistedDocument: true);
        Require(missingImages.IsWriteDisabled && !File.Exists(missingImagePath) &&
                !missingImages.TryGetEncodedImageBytes("202", out _, out _),
            "missing image database must stay untouched and reject unsafe image writes");

        // An unrelated missing picture is NOT a reason to disable a healthy LMDB.
        using var partialImages = new NoteImageStore(Path.Combine(scope.DirectoryPath, "note-assets-partial.lmdb"));
        partialImages.Load();
        Require(partialImages.ImportBitmapSource("note-a", WorkspaceBitmap(255, 0)).Id == "001",
            "image fixture did not start at the first ID");
        var protectedWithGap = new HashSet<string>(secondaryIds, StringComparer.Ordinal) { "002" };
        partialImages.ProtectPersistedImageReferences(second, protectedWithGap, hasPersistedDocument: true);
        Require(!partialImages.IsWriteDisabled &&
                partialImages.ImportBitmapSource("note-a", WorkspaceBitmap(0, 255)).Id == "003",
            "a dangling image ID was reused or blocked all healthy image imports");

        // A genuinely new, empty workspace must still be allowed to create its first LMDB.
        var freshImagePath = Path.Combine(scope.DirectoryPath, "note-assets-fresh.lmdb");
        using var freshImages = new NoteImageStore(freshImagePath);
        freshImages.Load();
        freshImages.ProtectPersistedImageReferences(new AppState(), new HashSet<string>(), false);
        Require(!freshImages.IsWriteDisabled &&
                freshImages.ImportBitmapSource("note-a", WorkspaceBitmap(255, 0)).Id == "001" &&
                File.Exists(freshImagePath),
            "a fresh workspace was mistaken for missing historical image data");

        // Damaged records are explicitly abandoned rather than permanently stopping
        // cleanup for all healthy images in the same workspace.
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "data2conflict-broken.json"), "{broken");
        Require(store2.TryCollectProtectedImageIds(second, out var stillProtected) &&
                stillProtected.Contains("303"),
            "a damaged legacy archive disabled the healthy workspace image scan");
        var uncertainPath = Path.Combine(scope.DirectoryPath, "note-assets-uncertain.lmdb");
        using var uncertain = new NoteImageStore(uncertainPath);
        uncertain.Load();
        var textOnly = NewState();
        textOnly.Papers[0].Content = "";
        uncertain.ProtectPersistedImageReferences(textOnly, null, hasPersistedDocument: true);
        Require(uncertain.IsWriteDisabled && !File.Exists(uncertainPath),
            "incomplete recovery scan must not permit recreating a missing image library");
    }

    private static void WorkspacePluginStores()
    {
        using var scope = new Scope();
        var root = Path.Combine(scope.DirectoryPath, "plugins");
        using (var first = new PaperBodyPluginDataStore(root, 1))
        using (var second = new PaperBodyPluginDataStore(root, 2))
        {
            first.SavePaperState("fixture", "same-paper", 1, "{\"slot\":1}");
            second.SavePaperState("fixture", "same-paper", 1, "{\"slot\":2}");
            first.DetachForWorkspaceSwitch(); second.DetachForWorkspaceSwitch();
            Throws<ObjectDisposedException>(() => first.ReadPaperState("fixture", "same-paper"));
            first.ResumeAfterWorkspaceSwitch();
            Require(J(first.ReadPaperState("fixture", "same-paper").Json)["slot"]!.GetValue<int>() == 1,
                "rollback did not restore cached plugin state");
        }
        using var reopened = new PaperBodyPluginDataStore(root, 2);
        Require(J(reopened.ReadPaperState("fixture", "same-paper").Json)["slot"]!.GetValue<int>() == 2,
            "plugin workspace data was not isolated on disk");
        var fail = true;
        using var failing = new PaperBodyPluginDataStore(Path.Combine(scope.DirectoryPath, "failure"),
            new DurableAtomicFileWriter((stage, _) =>
            {
                if (fail && stage == DurableAtomicWriteStage.BeforeTempOpen) throw new IOException("injected plugin write");
            }));
        failing.SaveRuntimeState("fixture", 1, "{\"kept\":true}");
        Throws<IOException>(failing.DetachForWorkspaceSwitch);
        Require(J(failing.ReadRuntimeState("fixture").Json)["kept"]!.GetValue<bool>(),
            "failed plugin detach discarded unsaved data");
        fail = false; failing.DetachForWorkspaceSwitch(); failing.ResumeAfterWorkspaceSwitch();
    }

    private static void WorkspacePreviewDoesNotAcceptSource()
    {
        using var scope = new Scope();
        var seed = scope.Store.Load();
        seed.Workspaces = new() { Enabled = true, Slots = [1, 2], Active = 1 };
        scope.Store.SaveJsonSync(scope.Store.SerializeState(seed), 2);
        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var active = catalog.OpenInitial();
        active.BindPersistence();
        active.State.Papers[0].Content = "last saved local";
        active.Store.SaveJsonSync(active.Store.SerializeState(active.State), 3);

        // Another editor changes the source AFTER the outgoing workspace saved.
        var disk = scope.Read();
        disk["papers"]![0]!["content"] = "external editor";
        scope.Write(disk);
        var target = catalog.StoreFor(2);
        target.SaveJsonSync(target.SerializeState(NewState()), 1);
        Require(target.TryRefreshBackupFromPrimary(), "busy target fixture needs a backup");
        var rejected = false;
        using (var busy = new FileStream(target.FilePath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            try { _ = catalog.Open(2, catalog.Options, main: active.State); }
            catch (IOException) { rejected = true; }
        }
        Require(rejected, "The inaccessible target must not fall back to its backup or initialize empty.");
        Require(active.State.Papers[0].Content != "external editor",
            "previewing a rejected candidate changed the active live state");
        active.Store.SaveJsonSync(active.Store.SerializeState(active.State), 4);
        Require(Str(scope.Read()["papers"]![0]!["content"]) == active.State.Papers[0].Content,
            "candidate failure introduced an external-detection hold on ordinary saves");
    }

    private static void WorkspaceStartupFallback()
    {
        using var scope = new Scope();
        var state = scope.Store.Load();
        state.Workspaces = new() { Enabled = true, Slots = [1, 2, 3], Active = 2 };
        scope.Store.SaveJsonSync(scope.Store.SerializeState(state), 2);
        var path = Path.Combine(scope.DirectoryPath, "data2.json");

        File.WriteAllText(path, "{broken");
        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared,
            (_, time) => time == null ? WorkspaceRecoveryChoice.CreateEmpty : WorkspaceRecoveryChoice.Cancel);
        var rebuilt = catalog.OpenInitial();
        Require(rebuilt.Paths.Number == 2 && catalog.StartupError == null &&
                rebuilt.State.Papers.Count == 1 && rebuilt.State.Papers[0].Content.Length == 0 &&
                rebuilt.Store.NeedsStartupRecoverySave &&
                catalog.Options.Active == 2 && File.ReadAllText(path) == "{broken" &&
                !Directory.GetFiles(scope.DirectoryPath, "data2.failed_load.*.json").Any(),
            "unrecoverable secondary did not stage a blank workspace or wrote it prematurely");

        var returning = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared,
            (_, time) => time == null ? WorkspaceRecoveryChoice.Fallback : WorkspaceRecoveryChoice.Cancel);
        var returned = returning.OpenInitial();
        Require(returned.Paths.Number == 1 && returning.Options.Active == 1 && returning.StartupError == null &&
                File.ReadAllText(path) == "{broken" &&
                new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared).Load().Workspaces!.Active == 1,
            "explicit startup fallback did not select healthy main or altered corrupt secondary");
        scope.Store.SaveJsonSync(scope.Store.SerializeState(state), 3);

        // Historical files, plugin data and a broken image library are not barriers
        // to creating a workspace whose primary and backup do not exist.
        File.Delete(path);
        var history = Path.Combine(scope.DirectoryPath, "data2conflict-history.json");
        File.WriteAllText(history, "{historical");
        var images = Path.Combine(scope.DirectoryPath, "note-assets2.lmdb");
        File.WriteAllText(images, "{not an image database");
        var pluginDir = Path.Combine(scope.DirectoryPath, "plugins", "data2");
        Directory.CreateDirectory(pluginDir);
        var missing = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var blank = missing.OpenInitial();
        Require(blank.Paths.Number == 2 && blank.State.Papers.Count == 1 &&
                blank.State.Papers[0].Content.Length == 0 && missing.StartupError == null &&
                File.ReadAllText(history) == "{historical" &&
                File.ReadAllText(images) == "{not an image database" &&
                Directory.Exists(pluginDir),
            "historical files or unrelated resources prevented blank initialization");

        // A busy data file is not corrupt. Leave the user's selected slot recorded
        // and show the existing fallback notice; retry must load the same data.
        File.WriteAllText(path, scope.Store.SerializeState(NewState()));
        var original = File.ReadAllBytes(path);
        using (var busy = new FileStream(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var fallback = blocked.OpenInitial();
            Require(fallback.Paths.Number == 1 && blocked.StartupError != null &&
                    blocked.Options.Active == 2,
                "busy secondary either reset the selected slot or bypassed the access error");
        }
        Require(File.ReadAllBytes(path).SequenceEqual(original), "busy workspace data changed");
        var retried = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        Require(retried.OpenInitial().Paths.Number == 2 && retried.StartupError == null,
            "retry after releasing the busy data file did not reopen the selected workspace");
    }

    private static int RunWorkspaceMissingImagesFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker)))
            throw new InvalidOperationException("Refusing startup fallback checks outside isolated fixture.");
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            AppController? controller = null;
            try
            {
                var main = NewState();
                main.UiLanguage = UiLanguages.English;
                main.PaperSkin = PaperSkins.Paper;
                main.Workspaces = new()
                {
                    Enabled = true, Slots = [1, 2], Active = 2,
                    FollowMainSettings = new() { [2] = false }
                };
                var independent = NewState();
                independent.UiLanguage = UiLanguages.Japanese;
                independent.PaperSkin = PaperSkins.Mica;
                independent.Papers[0].Content = MarkdownImageReferences.CreateReference("999");
                var writer = new StateStore();
                writer.SaveJsonSync(writer.SerializeState(main), 1);
                var secondary = new StateStore(AppContext.BaseDirectory, DurableAtomicFileWriter.Shared, 2);
                secondary.SaveJsonSync(secondary.SerializeState(independent), 1);
                controller = new AppController();
                Require(controller.WorkspaceNumber == 2 &&
                        Field<WorkspaceCatalog>(controller, "_workspaceCatalog").StartupError == null,
                    "missing images incorrectly forced a secondary workspace back to main");
                Require(UiLanguages.EffectiveUiCulture.Name == UiLanguages.Japanese &&
                        controller.State.Papers[0].Content.Contains("i:999", StringComparison.Ordinal),
                    "missing images changed the selected workspace's language or note");
                var images = Field<NoteImageStore>(controller, "_imageStore");
                Require(images.IsWriteDisabled &&
                        !File.Exists(Path.Combine(AppContext.BaseDirectory, "note-assets2.lmdb")) &&
                        !images.TryGetEncodedImageBytes("999", out _, out _),
                    "startup recreated missing image history or claimed that broken image bytes existed");
                await controller.StartAsync(createDefaultPaper: false);
                Require(controller.WorkspaceNumber == 2, "startup discarded selected workspace after rendering");
                Console.WriteLine("PASS startup remains in secondary workspace with missing pictures and no unsafe image writes");
                result = 0;
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { controller?.Dispose(); app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static int RunWorkspaceFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker)))
            throw new InvalidOperationException("Refusing workspace checks outside isolated fixture.");
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await WorkspaceLiveChecks(); result = 0; }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static BitmapSource WorkspaceBitmap(byte red, byte blue) => BitmapSource.Create(
        2, 2, 96, 96, PixelFormats.Bgra32, null,
        new byte[] { blue, 0, red, 255, blue, 0, red, 255, blue, 0, red, 255, blue, 0, red, 255 }, 8);

    private static async Task WorkspaceSettingsInteractionChecks(AppController controller)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var build = typeof(AppController).GetMethod("BuildWorkspaceSettingsPage", flags)!;
        var cancelRename = typeof(AppController).GetMethod("CancelWorkspaceRenameOnPointer", flags)!;
        Grid Page() => (Grid)build.Invoke(controller, null)!;
        StackPanel Options(Grid page) => page.Children.OfType<StackPanel>()
            .Single(column => Grid.GetColumn(column) == 0).Children.OfType<StackPanel>().Single();
        Border Slot(Grid page) => Options(page).Children.OfType<Border>().Single(card =>
            AutomationProperties.GetAutomationId(card) == "WorkspaceSlot2");

        var original = controller.WorkspaceOptions.Normalized();
        var originalName = original.DisplayName(2);
        var page = Page();
        var window = new Window { Content = page, Width = 630, Height = 400, ShowInTaskbar = false };
        window.Show();
        var editingCard = Slot(page);
        var cardControls = LogicalDescendants(editingCard).ToArray();
        var rename = cardControls.OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button) == "WorkspaceRename2");
        var editor = cardControls.OfType<TextBox>().Single();
        async Task<Rect> RenameButtonBoundsAsync()
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            return rename.TransformToAncestor(editingCard).TransformBounds(new Rect(rename.RenderSize));
        }

        rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var shortNameButton = await RenameButtonBoundsAsync();
        editor.Text = "Weekly plans";
        var mediumNameButton = await RenameButtonBoundsAsync();
        Require(mediumNameButton.Left > shortNameButton.Left,
            $"the save button did not follow the name: {shortNameButton} -> {mediumNameButton}");
        editor.Text = new string('W', 48);
        var longNameButton = await RenameButtonBoundsAsync();
        Require(longNameButton.Top >= shortNameButton.Bottom,
            $"the save button did not wrap: {shortNameButton} -> {longNameButton}");
        cancelRename.Invoke(controller, [new Border()]);
        Require(editor.Visibility == Visibility.Collapsed &&
                controller.WorkspaceOptions.DisplayName(2) == originalName,
            "clicking outside a workspace name editor saved its draft");

        rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        editor.Text = "Confirmed workspace name";
        rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => controller.WorkspaceOptions.DisplayName(2) == "Confirmed workspace name",
            "workspace rename confirmation did not save");
        Require(controller.WorkspaceNumber == 1, "renaming a workspace unexpectedly switched it");
        window.Close();

        var card = Slot(Page());
        card.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent
        });
        Require(controller.WorkspaceNumber == 1, "selecting a workspace card switched without confirmation");
        var confirm = Options(Page()).Children.OfType<Button>().Single();
        Require(confirm.IsEnabled, "selecting a workspace card did not enable switch confirmation");
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => controller.WorkspaceNumber == 2 && !controller.IsWorkspaceSwitching,
            "confirmed settings switch did not reach the selected workspace");
        Require(await controller.SwitchWorkspaceAsync(1), "return after settings switch failed");
        Require(await controller.ChangeWorkspaceConfigurationAsync(original) &&
                controller.WorkspaceOptions.DisplayName(2) == originalName,
            "workspace settings test did not restore the previous name");
        Console.WriteLine("PASS workspace settings: rename cancel/confirm and explicit card switch confirmation");
    }

    private static async Task WorkspaceLiveChecks()
    {
        var directory = AppContext.BaseDirectory;
        var mainStore = new StateStore();
        var main = NewState();
        main.Theme = "dark"; main.UiLanguage = "en-US";
        main.McpEnabled = main.McpAllowFullWrites = true;
        main.Workspaces = new() { Enabled = true, Slots = [1, 2, 3, 4, 5], Names = new() { [2] = "11" } };
        var second = NewState();
        second.Theme = "light"; second.UiLanguage = "ja-JP";
        second.McpEnabled = second.McpAllowFullWrites = false;
        // Reuse IDs deliberately: a slot boundary must not reuse old windows, undo or bitmaps.
        string imageId;
        byte[] mainImage;
        using (var images = new NoteImageStore())
        {
            images.Load();
            imageId = images.ImportBitmapSource("note-a", WorkspaceBitmap(255, 0)).Id;
            Require(images.TryGetEncodedImageBytes(imageId, out _, out mainImage!), "main image not stored");
        }
        byte[] secondImage;
        using (var images = new NoteImageStore(Path.Combine(directory, "note-assets2.lmdb")))
        {
            images.Load();
            var id = images.ImportBitmapSource("note-a", WorkspaceBitmap(0, 255)).Id;
            Require(id == imageId, "fixture must exercise identical cross-workspace asset IDs");
            Require(images.TryGetEncodedImageBytes(id, out _, out secondImage!), "secondary image not stored");
        }
        main.Papers[0].Content = $"main\n![image](i:{imageId})";
        second.Papers[0].Content = $"second\n![image](i:{imageId})";
        mainStore.SaveJsonSync(mainStore.SerializeState(main), 1);
        var store2 = new StateStore(directory, DurableAtomicFileWriter.Shared, 2);
        store2.SaveJsonSync(store2.SerializeState(second), 1);
        var pluginRoot = Path.Combine(directory, "plugins");
        using (var data = new PaperBodyPluginDataStore(pluginRoot, 1))
            data.SavePaperState("fixture", "note-a", 1, "{\"slot\":1}");
        using (var data = new PaperBodyPluginDataStore(pluginRoot, 2))
            data.SavePaperState("fixture", "note-a", 1, "{\"slot\":2}");

        var controller = new AppController();
        try
        {
            await controller.StartAsync(createDefaultPaper: false);
            var windows = Field<Dictionary<string, PaperWindow>>(controller, "_windows");
            foreach (var window in windows.Values) window.EnsureShellBuilt();
            await Dispatcher.Yield(DispatcherPriority.Background);
            controller.PaperCommands.ReloadData(PaperOperationContext.Mcp());
            await Until(() => !controller.IsDataReloading, "initial reload did not settle");
            Require(controller.PaperCommands.GetDataReloadStatus().LastResult != null,
                "fixture did not establish a previous-workspace reload result");
            var oldWindow = windows["note-a"];
            var oldStore = Field<StateStore>(controller, "_store");
            var draft = $"saved draft\n![image](i:{imageId})";
            var editor = Field<MarkdownTextBox>(oldWindow, "_noteBox");
            editor.Text = draft;
            // The existing editor adds a trailing caret-anchor line after a final image.
            // Compare persisted bytes to the actual editor text, not an unnormalized fixture.
            var expectedDraft = editor.PersistentText;
            Require(expectedDraft.Replace("\r\n", "\n").TrimEnd('\n') == draft,
                "unexpected fixture editor normalization: " + JsonSerializer.Serialize(expectedDraft));
            var previousMcp = new McpCommandService(controller, controller.PaperCommands);
            // Emulate the persistent stdio MCP bridge: each pipe request reaches the CURRENT
            // adapter, while the calling bridge holds the workspace token from its first query.
            var boundPipeName = "PaperTodo-workspace-binding-" + Guid.NewGuid().ToString("N");
            using var boundPipe = new McpApiHost(Dispatcher.CurrentDispatcher,
                request => new McpCommandService(controller, controller.PaperCommands).ExecutePipeBound(request),
                boundPipeName);
            boundPipe.Start();
            var oldBridge = new McpPipeClient(boundPipeName);
            _ = await oldBridge.InvokeAsync("get_data_reload_status", new { }, CancellationToken.None);
            var originalScript = controller.State.TopBarScript;
            controller.State.TopBarScript = "!ps5\nStart-Sleep -Seconds 60";
            try { controller.RunTopBarScript(oldWindow); }
            finally { controller.State.TopBarScript = originalScript; }
            // Create a real idle session without making all later fixture switches inherit it.
            ScriptExecutionService.EnsurePersistentScriptProcessForSettings(new AppState
            {
                UsePersistentPowerShellProcess = true, PreferPowerShell7 = false, HideScriptRunWindow = true
            });
            var activeScripts = (Dictionary<Guid, Process>)typeof(ScriptExecutionService)
                .GetField("ActiveScriptProcesses", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var persistentScripts = (Dictionary<string, Process>)typeof(ScriptExecutionService)
                .GetField("PersistentScriptProcesses", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            using var runningScript = Process.GetProcessById(activeScripts.Values.Single().Id);
            using var idleSession = Process.GetProcessById(persistentScripts.Values.Single().Id);
            controller.SaveNow(); // deliberately leave an asynchronous ordinary write queued
            Require(controller.State.Papers.Single(p => p.Id == "note-a").Content == expectedDraft,
                "ordinary save did not commit the fixture editor");
            var stopNotices = 0;
            Require(await WithWorkspaceFailureDialog(() => controller.CycleWorkspaceAsync(), dialog =>
            {
                stopNotices++;
                Require(controller.WorkspaceNumber == 2 && runningScript.HasExited && idleSession.HasExited,
                    "script stop notice appeared before target activation or source process termination");
                Require(FailureDialogNodes(dialog).OfType<TextBlock>().Any(block =>
                    block.Text == Strings.Get("WorkspaceScriptsAndSessionsStoppedMessage")),
                    "workspace notice did not distinguish stopped scripts and closed idle sessions");
                ClickFailureAction(dialog, "CommonOk");
            }, "WorkspaceScriptsStoppedTitle"), "first cycle failed");
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(stopNotices == 1 && !Application.Current.Windows.OfType<Window>().Any(window =>
                    window.IsVisible && window.Title == Strings.Get("WorkspaceScriptsStoppedTitle")),
                "successful workspace switch repeated its script stop notice");
            Require(controller.WorkspaceNumber == 2 && oldWindow.IsClosed && !ReferenceEquals(oldWindow, windows["note-a"]),
                "workspace switch reused the old paper window");
            Require(controller.State.Theme == "dark" && controller.State.McpEnabled &&
                UiLanguages.EffectiveUiCulture.Name == "en-US", "default inheritance missing");
            var switchedStatus = controller.PaperCommands.GetDataReloadStatus();
            Require(switchedStatus.LastResult == null && switchedStatus.LastAttemptAt == null,
                "workspace switch retained the previous workspace's explicit reload history");
            var savedDraft = Str(J(File.ReadAllText(mainStore.FilePath))["papers"]![0]!["content"]);
            Require(savedDraft == expectedDraft,
                "pending editor text was lost on departure: " + JsonSerializer.Serialize(savedDraft));
            Throws<McpApiException>(() => previousMcp.Execute(JsonSerializer.SerializeToElement(new { method = "list_papers" })));
            var currentMcp = new McpCommandService(controller, controller.PaperCommands);
            Require(currentMcp.Execute(JsonSerializer.SerializeToElement(new { method = "list_papers" })) != null,
                "new-workspace MCP adapter could not read current papers");
            var staleRejected = false;
            try { _ = await oldBridge.InvokeAsync("reload_data", new { }, CancellationToken.None); }
            catch (Exception ex) when (ex.Message.Contains("workspace_changed", StringComparison.Ordinal))
            {
                staleRejected = true;
            }
            Require(staleRejected, "a new pipe request from the old MCP bridge retargeted the new workspace");
            // An EXPLICIT new-workspace status query renews the binding through the
            // private pipe envelope. It never retries a write that already failed above.
            var refreshed = await oldBridge.InvokeAsync("get_data_reload_status", new { }, CancellationToken.None);
            Require(refreshed.Deserialize<DataReloadStatus>()?.WorkspaceNumber == 2,
                "stale MCP bridge did not explicitly observe the new workspace");
            var accepted = await oldBridge.InvokeAsync("reload_data", new { }, CancellationToken.None);
            Require(accepted.Deserialize<DataReloadResult>()?.Outcome is "applied" or "unchanged",
                "status acknowledgement did not renew the bridge's workspace lease");
            await Until(() => !controller.IsDataReloading, "MCP bridge renewed reload");
            var newBridge = new McpPipeClient(boundPipeName);
            _ = await newBridge.InvokeAsync("get_setting", new { id = "appearance.theme" }, CancellationToken.None);
            Console.WriteLine("PASS MCP bridge rejects stale writes and rebinds after explicit workspace status");
            Require(controller.ImageStore.TryGetEncodedImageBytes(imageId, out _, out var bytes2) && bytes2.SequenceEqual(secondImage),
                "new workspace decoded old image bytes");
            Require(J(controller.PaperBodyPlugins.DataStore.ReadPaperState("fixture", "note-a").Json)["slot"]!.GetValue<int>() == 2,
                "plugin per-paper state did not switch");
            var stale = mainStore.SerializeState(main);
            oldStore.SaveJsonAsync(stale, 1).GetAwaiter().GetResult();
            Require(J(File.ReadAllText(mainStore.FilePath))["workspaces"]!["active"]!.GetValue<int>() == 2,
                "old asynchronous writer reverted main catalog");
            var workspaceStatus = controller.PaperCommands.GetDataReloadStatus();
            Require(workspaceStatus.WorkspaceNumber == 2 && workspaceStatus.DataFileName == "data2.json" &&
                workspaceStatus.FollowsMainSettings && !workspaceStatus.IsReloading &&
                workspaceStatus.LastResult?.Applied == true && workspaceStatus.LastAttemptAt != null,
                "post-rebind status does not identify the actual reload target");
            Require(!controller.PublicSettings.Get("appearance.theme").Writable, "inherited setting advertised writable");

            Throws<PaperSettingsException>(() => controller.PublicSettings.Set("appearance.theme", JsonSerializer.SerializeToElement("light")));
            controller.SaveNow(sync: true);
            Require(Str(J(File.ReadAllText(store2.FilePath))["theme"]) == "light", "inherited settings saved over dormant settings");

            // Catalog-only edits must not flush the active secondary's core data.
            var secondaryStore = Field<WorkspaceCatalog>(controller, "_workspaceCatalog").StoreFor(2);
            var secondaryWriterField = typeof(StateStore).GetField("_atomicWriter",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalSecondaryWriter = (IDurableAtomicFileWriter)secondaryWriterField.GetValue(secondaryStore)!;
            var beforeCatalogEdit = File.ReadAllBytes(store2.FilePath);
            var earlierSlots = controller.WorkspaceOptions.SlotNumbers.ToArray();
            try
            {
                secondaryWriterField.SetValue(secondaryStore, new DurableAtomicFileWriter((stage, path) =>
                {
                    if (stage == DurableAtomicWriteStage.BeforeTempOpen && path == secondaryStore.FilePath)
                        throw new IOException("Catalog update must not save secondary content.");
                }));
                var changedMembership = controller.WorkspaceOptions.Normalized();
                changedMembership.Slots = earlierSlots.Contains(5)
                    ? earlierSlots.Where(n => n != 5).ToList()
                    : earlierSlots.Append(5).OrderBy(n => n).ToList();
                Require(await controller.ChangeWorkspaceConfigurationAsync(changedMembership),
                    "secondary catalog-only update triggered a secondary save");
                Require(File.ReadAllBytes(store2.FilePath).SequenceEqual(beforeCatalogEdit),
                    "secondary catalog-only update unexpectedly modified its document");
            }
            finally { secondaryWriterField.SetValue(secondaryStore, originalSecondaryWriter); }
            var restoredMembership = controller.WorkspaceOptions.Normalized();
            restoredMembership.Slots = earlierSlots.ToList();
            Require(await controller.ChangeWorkspaceConfigurationAsync(restoredMembership),
                "secondary catalog-only restoration failed");

            // A follow toggle changes only the effective software settings. It must not
            // destroy editors, image handles, plugin state or the workspace binding.
            var followingWindow = windows["note-a"];
            var followingState = controller.State;
            var followingImages = controller.ImageStore;
            var followingPluginData = controller.PaperBodyPlugins.DataStore;
            var followingBinding = controller.WorkspaceBindingToken;
            var independent = controller.WorkspaceOptions.Normalized(); independent.FollowMainSettings[2] = false;
            Require(await controller.ChangeWorkspaceConfigurationAsync(independent), "unfollow failed");
            Require(ReferenceEquals(controller.State, followingState) &&
                    ReferenceEquals(windows["note-a"], followingWindow) && !followingWindow.IsClosed &&
                    ReferenceEquals(controller.ImageStore, followingImages) &&
                    ReferenceEquals(controller.PaperBodyPlugins.DataStore, followingPluginData) &&
                    controller.WorkspaceBindingToken == followingBinding,
                "unfollow rebuilt the workspace instead of updating its settings");
            Require(controller.State.Theme == "light" && !controller.State.McpEnabled &&
                UiLanguages.EffectiveUiCulture.Name == "ja-JP" && controller.PublicSettings.Get("appearance.theme").Writable,
                "independent software settings/language did not activate");
            var following = controller.WorkspaceOptions.Normalized(); following.FollowMainSettings[2] = true;
            Require(await controller.ChangeWorkspaceConfigurationAsync(following), "refollow failed");
            Require(controller.State.Theme == "dark" && controller.State.McpEnabled, "refollow did not reapply main settings");
            Require(ReferenceEquals(controller.State, followingState) &&
                    ReferenceEquals(windows["note-a"], followingWindow) && !followingWindow.IsClosed &&
                    controller.WorkspaceBindingToken == followingBinding,
                "refollow unnecessarily recreated the active workspace");
            Require(await controller.SwitchWorkspaceAsync(1), "return to main failed");
            // The second slot has already been accepted and normalized. Re-entering
            // an unchanged workspace must succeed without writing its content file.
            var secondWriterField = typeof(StateStore).GetField("_atomicWriter",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var storedSecond = Field<WorkspaceCatalog>(controller, "_workspaceCatalog").StoreFor(2);
            var previousSecondWriter = (IDurableAtomicFileWriter)secondWriterField.GetValue(storedSecond)!;
            try
            {
                secondWriterField.SetValue(storedSecond, new DurableAtomicFileWriter((stage, path) =>
                {
                    if (stage == DurableAtomicWriteStage.BeforeTempOpen && path == storedSecond.FilePath)
                        throw new IOException("Healthy target should not be rewritten on entry.");
                }));
                Require(await controller.SwitchWorkspaceAsync(2),
                    "re-entering an unchanged secondary unexpectedly saved its core data");
            }
            finally { secondWriterField.SetValue(storedSecond, previousSecondWriter); }
            Require(await controller.SwitchWorkspaceAsync(1), "return after healthy read-only entry failed");
            Require(controller.ImageStore.TryGetEncodedImageBytes(imageId, out _, out var bytes1) && bytes1.SequenceEqual(mainImage),
                "main image changed across roundtrip");
            Require(J(controller.PaperBodyPlugins.DataStore.ReadPaperState("fixture", "note-a").Json)["slot"]!.GetValue<int>() == 1,
                "leaving a workspace deleted its plugin state");

            var clickPaper = windows["note-a"];
            clickPaper.EnsureShellBuilt();
            var cycleButton = Field<Button>(clickPaper, "_workspaceButton");
            Require(cycleButton.Visibility == Visibility.Visible &&
                Field<TextBlock>(clickPaper, "_workspaceNumberBadge").Text == "1", "wrong top-bar badge");
            cycleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => controller.WorkspaceNumber == 2 && !controller.IsWorkspaceSwitching, "top-bar click did not switch");
            Require(await controller.SwitchWorkspaceAsync(1), "return after actual button event failed");
            await WorkspaceSettingsInteractionChecks(controller);

            await WorkspaceMainCatalogSingleWriteChecks(controller);
            await WorkspaceRollbackChecks(controller, directory);

            // Restore can legitimately expand a saved capsule when the effective
            // settings hide todo-linked papers from capsules. That UI change must
            // persist, while a healthy unchanged target must still avoid writes.
            var beforeRestoreOptions = controller.WorkspaceOptions.Normalized();
            var localThirdOptions = controller.WorkspaceOptions.Normalized();
            localThirdOptions.FollowMainSettings[3] = false;
            Require(await controller.ChangeWorkspaceConfigurationAsync(localThirdOptions),
                "restore mutation fixture could not enable independent third-slot settings");
            var thirdState = NewState();
            thirdState.UseCapsuleMode = true;
            thirdState.EnableTodoPaperLinks = true;
            thirdState.HideLinkedPapersFromCapsules = true;
            thirdState.Papers[0].IsCollapsed = true;
            thirdState.Papers[1].Items[0].RestoreQuickLaunch("note-a", null, false);
            var thirdDataPath = Path.Combine(directory, "data3.json");
            var thirdStore = Field<WorkspaceCatalog>(controller, "_workspaceCatalog").StoreFor(3);
            File.WriteAllText(thirdDataPath, thirdStore.SerializeState(thirdState));
            var restoreWriterField = typeof(StateStore).GetField("_atomicWriter",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalRestoreWriter = (IDurableAtomicFileWriter)restoreWriterField.GetValue(thirdStore)!;
            var restorationWrites = 0;
            try
            {
                restoreWriterField.SetValue(thirdStore, new DurableAtomicFileWriter((stage, path) =>
                {
                    if (stage == DurableAtomicWriteStage.BeforeTempOpen && path == thirdStore.FilePath)
                        restorationWrites++;
                }));
                Require(await controller.SwitchWorkspaceAsync(3), "restore mutation fixture could not enter slot 3");
                Require(!controller.State.Papers[0].IsCollapsed && restorationWrites == 1 &&
                        !J(File.ReadAllText(thirdDataPath))["papers"]![0]!["isCollapsed"]!.GetValue<bool>(),
                    "presentation expanded a blocked capsule without saving its new state exactly once");
            }
            finally { restoreWriterField.SetValue(thirdStore, originalRestoreWriter); }
            Require(await controller.SwitchWorkspaceAsync(1), "restore mutation fixture could not return to main");
            Require(await controller.ChangeWorkspaceConfigurationAsync(beforeRestoreOptions),
                "restore mutation fixture could not restore follow settings");

            // Missing images remain a local rendering failure, not a reason to reject
            // an otherwise valid workspace. Nor may a missing LMDB be replaced on entry.
            var fourthDataPath = Path.Combine(directory, "data4.json");
            var fourthState = NewState();
            fourthState.Papers[0].Content = MarkdownImageReferences.CreateReference("999");
            File.WriteAllText(fourthDataPath, store2.SerializeState(fourthState));
            var fourthImagesPath = Path.Combine(directory, "note-assets4.lmdb");
            Require(!File.Exists(fourthImagesPath), "missing-image switch fixture unexpectedly has an LMDB");
            Require(await controller.SwitchWorkspaceAsync(4) && controller.WorkspaceNumber == 4 &&
                    controller.State.Papers[0].Content.Contains("i:999", StringComparison.Ordinal) &&
                    controller.ImageStore.IsWriteDisabled && !File.Exists(fourthImagesPath),
                "missing pictures blocked workspace entry or created an unsafe image database");
            Require(!controller.ImageStore.TryGetEncodedImageBytes("999", out _, out _),
                "missing image incorrectly rendered as available");
            Require(await controller.SwitchWorkspaceAsync(1), "return after missing-image switch failed");

            // Locked-target dismissal is covered by WorkspaceFailureChecks.
            var busyFifthPath = Path.Combine(directory, "data5.json");

            // With no usable source, blank initialization requires an explicit choice.
            File.WriteAllText(busyFifthPath, "{invalid target");
            Require(await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(5),
                    dialog => ClickFailureAction(dialog, "HistoryRecoveryCreate"), "HistoryRecoveryTitle") &&
                    controller.WorkspaceNumber == 5 &&
                    controller.State.Papers.Count == 1 && controller.State.Papers[0].Content.Length == 0,
                "unrecoverable fifth workspace did not initialize blank");
            Require(await controller.SwitchWorkspaceAsync(1), "return after explicit rebuild failed");
            File.WriteAllText(busyFifthPath, "{invalid target");
            File.Delete(Path.Combine(directory, "data5.backup.json"));
            var fifthRecovered = NewState();
            fifthRecovered.Papers[0].Content = "automatically recovered fifth";
            File.WriteAllText(busyFifthPath + ".tmp", store2.SerializeState(fifthRecovered));
            Require(await controller.SwitchWorkspaceAsync(5) && controller.WorkspaceNumber == 5 &&
                    controller.State.Papers[0].Content == "automatically recovered fifth" &&
                    !File.Exists(busyFifthPath + ".tmp") &&
                    !Directory.GetFiles(directory, "data5.failed_load.*.json").Any(),
                "complete fifth-workspace temp was not automatically recovered");
            var fifthPath = Path.Combine(directory, "data5.json");
            var fifthId = controller.State.Papers[0].Id;
            var reduced = controller.WorkspaceOptions.Normalized(); reduced.Slots = [1, 2, 3];
            Require(await controller.ChangeWorkspaceConfigurationAsync(reduced) && controller.WorkspaceNumber == 1,
                "removing a slot did not return an excluded active workspace to main");
            Require(File.Exists(fifthPath) && Str(J(File.ReadAllText(fifthPath))["papers"]![0]!["id"]) == fifthId,
                "removing a slot unexpectedly deleted its data");
            Require(await controller.CycleWorkspaceAsync() && controller.WorkspaceNumber == 2, "cycle to2 failed");
            Require(await controller.CycleWorkspaceAsync() && controller.WorkspaceNumber == 3, "cycle to3 failed");
            Require(await controller.CycleWorkspaceAsync() && controller.WorkspaceNumber == 1, "cycle did not wrap at the last active slot");

            // Only existing workspaces and one create action occupy the row.
            // Creating a slot never switches away and reuses the first missing number.
            ContentPresenter TraySegmentHost(MenuItem item) =>
                LogicalDescendants((DependencyObject)item.Header).OfType<ContentPresenter>().Single();
            Border[] SegmentTiles(ContentPresenter host) =>
                LogicalDescendants((DependencyObject)host.Content).OfType<Border>()
                    .Where(border => border.Child is TextBlock or System.Windows.Shapes.Path || border.Child == null)
                    .ToArray();
            ContextMenu TrayForCreate()
            {
                var menu = controller.CreateTrayMenu();
                controller.RebuildTrayMenu(menu);
                return menu;
            }
            Border[] TraySlots(ContextMenu menu)
            {
                var workspaceItem = menu.Items.OfType<MenuItem>().Single(item =>
                    AutomationProperties.GetAutomationId(item) == "TrayWorkspaceSelector");
                return SegmentTiles(TraySegmentHost(workspaceItem));
            }
            void ClickTraySlot(Border tile) => tile.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            });

            var originalWorkspaceState = controller.State;
            var threeSlotMenu = TrayForCreate();
            var threeTiles = TraySlots(threeSlotMenu);
            Require(threeTiles.Length == 4 &&
                    ((TextBlock)threeTiles[0].Child).Text == "1" &&
                    ((TextBlock)threeTiles[1].Child).Text == "2" &&
                    ((TextBlock)threeTiles[2].Child).Text == "3" &&
                    threeTiles[3].Child is System.Windows.Shapes.Path { Data: not null } &&
                    AutomationProperties.GetAutomationId(threeTiles[3]) == "TrayCreateWorkspace",
                "tray must show existing workspaces and one create action without empty slots");
            threeSlotMenu.IsOpen = true;
            await Until(() => threeSlotMenu.ActualWidth > 0, "tray create menu did not finish layout");
            threeSlotMenu.Focus();
            Require(!await controller.SwitchWorkspaceAsync(2) && controller.WorkspaceNumber == 1,
                "an open menu must still block a workspace window handoff");
            ClickTraySlot(threeTiles[3]);
            await Until(() => controller.WorkspaceOptions.ContainsSlot(4) && !controller.IsWorkspaceSwitching,
                "tray plus did not create workspace 4");
            Require(controller.WorkspaceNumber == 1 && ReferenceEquals(controller.State, originalWorkspaceState),
                "creating a tray workspace changed the active workspace or its state");
            Require(threeSlotMenu.IsOpen && threeSlotMenu.IsKeyboardFocusWithin &&
                    ((TextBlock)TraySlots(threeSlotMenu)[3].Child).Text == "4",
                "creating a workspace dismissed the tray, lost focus or left stale slots");
            threeSlotMenu.IsOpen = false;

            var fourTiles = TraySlots(TrayForCreate());
            Require(((TextBlock)fourTiles[3].Child).Text == "4" &&
                    fourTiles[4].Child is System.Windows.Shapes.Path,
                "tray plus did not move to the next vacant position");
            ClickTraySlot(fourTiles[4]);
            await Until(() => controller.WorkspaceOptions.ContainsSlot(5) && !controller.IsWorkspaceSwitching,
                "tray plus did not create workspace 5");
            Require(controller.WorkspaceNumber == 1 &&
                    TraySlots(TrayForCreate()).All(tile => tile.Child is TextBlock),
                "five full workspace slots still showed a create action or switched workspaces");

            var withGap = controller.WorkspaceOptions.Normalized();
            withGap.Slots.Remove(2);
            Require(await controller.ChangeWorkspaceConfigurationAsync(withGap),
                "could not remove an intermediate slot for tray gap test");
            var gapTiles = TraySlots(TrayForCreate());
            Require(gapTiles[^1].Child is System.Windows.Shapes.Path &&
                    ((TextBlock)gapTiles[1].Child).Text == "3",
                "tray did not offer to reuse the first missing workspace number");
            ClickTraySlot(gapTiles[^1]);
            await Until(() => controller.WorkspaceOptions.ContainsSlot(2) && !controller.IsWorkspaceSwitching,
                "tray plus did not reuse the missing workspace number");
            Require(controller.WorkspaceNumber == 1 && ReferenceEquals(controller.State, originalWorkspaceState),
                "recreating an intermediate workspace unexpectedly switched workspace");

            var cancelMenu = TrayForCreate();
            var selector = cancelMenu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetAutomationId(item) == "TrayWorkspaceSelector");
            var segmentHost = TraySegmentHost(selector);
            cancelMenu.IsOpen = true;
            await Until(() => cancelMenu.ActualWidth > 0 && selector.ActualWidth > 0,
                "tray workspace menu did not finish layout");
            var menuWidth = cancelMenu.ActualWidth;
            ClickTraySlot(TraySlots(cancelMenu)[1]);
            Require(controller.WorkspaceNumber == 1,
                "tray workspace choice switched without confirmation");
            var cancelActions = LogicalDescendants((DependencyObject)segmentHost.Content).OfType<Button>().ToArray();
            Require(cancelActions.Length == 2, "tray did not offer switch and cancel actions");
            cancelMenu.UpdateLayout();
            Require(Math.Abs(cancelMenu.ActualWidth - menuWidth) < 1 && cancelActions.All(button =>
            {
                var bounds = button.TransformToAncestor(cancelMenu).TransformBounds(new Rect(button.RenderSize));
                return button.IsVisible && bounds.Left >= 0 && bounds.Right <= cancelMenu.ActualWidth &&
                    bounds.Top >= 0 && bounds.Bottom <= cancelMenu.ActualHeight;
            }), "workspace confirmation widened the menu or clipped an action button");
            cancelActions[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(controller.WorkspaceNumber == 1 && segmentHost.Content is Border,
                "cancelling the tray workspace choice switched or retained the confirmation");
            cancelMenu.IsOpen = false;

            Console.WriteLine("PASS workspace WPF: cycle, editor flush, ID collision, inheritance, language, plugin data, failures and slot retention");
        }
        finally { controller.Dispose(); }
    }
}
