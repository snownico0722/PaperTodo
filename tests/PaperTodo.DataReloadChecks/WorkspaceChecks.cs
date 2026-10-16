using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void WorkspaceStorageChecks()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("legacy main and numbered paths", WorkspacePathsAndLegacy),
            ("main catalog updates preserve content and fence old writers", WorkspaceMetadataWrites),
            ("follow-main overlay preserves dormant preferences and layout", WorkspaceInheritance),
            ("image recovery protection is per workspace", WorkspaceImageProtection),
            ("plugin detach flush is strict and recoverable", WorkspacePluginStores),
            ("bad startup workspace falls back without replacing it", WorkspaceStartupFallback),
            ("plugin language fallback restores discovered labels", WorkspacePluginLocalization),
            ("inactive following state is released but reload updates its settings projection", WorkspaceSessionRelease)
        };
        foreach (var check in checks)
        {
            check.Run();
            Console.WriteLine("PASS workspace: " + check.Name);
        }
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
        var options = new WorkspaceConfiguration { Enabled = true, Count = 99, Active = 99 }.Normalized();
        Require(options.Count == 5 && options.Active == 5 && options.FollowsMain(2) && !options.FollowsMain(1),
            "invalid limits or follow default");
        Require(!File.Exists(Path.Combine(scope.DirectoryPath, "workspaces.json")), "unexpected separate catalog");
    }

    private static void WorkspaceMetadataWrites()
    {
        using var scope = new Scope();
        var source = scope.Read();
        source["futureExtension"] = J("{\"keep\":123}");
        scope.Write(source);
        var options = new WorkspaceConfiguration { Enabled = true, Count = 5, Active = 3 };
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
            new WorkspaceConfiguration { Enabled = true, Count = 2, Active = 2 }, 7));
        Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(bytes), "failed catalog write replaced main");
    }

    private static void WorkspaceInheritance()
    {
        using var scope = new Scope();
        var main = scope.Store.Load();
        main.Theme = "dark";
        main.MaxTitleLength = 20;
        main.UseCapsuleMode = main.UseDeepCapsuleMode = true;
        main.McpEnabled = main.McpAllowFullWrites = true;
        main.Workspaces = new() { Enabled = true, Count = 3 };
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
        var session = catalog.Open(2, catalog.Options, false);
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
        var next = catalog.Open(2, independent, false);
        next.BindPersistence();
        Require(next.State.Theme == "light" && !next.State.McpEnabled && !next.FollowsMain,
            "unfollow did not restore independent settings");
        var newSlot = catalog.Open(3, catalog.Options, true);
        Require(newSlot.State.Papers.Count == 1 && newSlot.State.Papers[0].Content.Length == 0 &&
            newSlot.State.Papers[0].Id != main.Papers[0].Id, "new workspace copied main content");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BindRetiredWorkspace(WorkspaceCatalog catalog)
    {
        var session = catalog.Open(2, catalog.Options, false);
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
        var session = catalog.Open(2, catalog.Options, false);
        session.BindPersistence();
        var incoming = NewState(); incoming.Theme = "light";
        var effective = catalog.AcceptReload(session, incoming);
        StateReloadModels.Apply(session.State, effective);
        Require(Str(J(session.Store.SerializeState(session.State))["theme"]) == "light",
            "reload kept a stale independent-settings projection");
        GC.KeepAlive(catalog);
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

    private static void WorkspaceStartupFallback()
    {
        using var scope = new Scope();
        var state = scope.Store.Load();
        state.Workspaces = new() { Enabled = true, Count = 3, Active = 2 };
        scope.Store.SaveJsonSync(scope.Store.SerializeState(state), 2);
        var path = Path.Combine(scope.DirectoryPath, "data2.json");
        File.WriteAllText(path, "{broken");
        var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var session = catalog.OpenInitial();
        Require(session.Paths.Number == 1 && catalog.StartupError != null && File.ReadAllText(path) == "{broken",
            "bad secondary was replaced or startup could not retain main");
        File.Delete(path);
        var missing = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        Require(missing.OpenInitial().Paths.Number == 1 && !File.Exists(path), "missing startup slot was silently recreated");
    }

    private static void WorkspacePluginLocalization()
    {
        var manifest = new PaperBodyPluginManifest
        {
            Name = "original", Description = "base detail",
            Locales = new()
            {
                ["zh-CN"] = new() { Name = "中文", Description = "说明" },
                ["ja-JP"] = new() { Name = "日本語" }
            }
        };
        PaperBodyPluginLocalization.ValidateAndApply(manifest, CultureInfo.GetCultureInfo("zh-CN"));
        PaperBodyPluginLocalization.ValidateAndApply(manifest, CultureInfo.GetCultureInfo("ja-JP"));
        Require(manifest.Name == "日本語" && manifest.Description == "base detail", "locale fallback leaked old language");
        PaperBodyPluginLocalization.ValidateAndApply(manifest, CultureInfo.GetCultureInfo("en-US"));
        Require(manifest.Name == "original", "missing locale did not restore source labels");
    }

    private static int RunWorkspaceFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker)))
            throw new InvalidOperationException("Refusing workspace checks outside isolated fixture.");
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await WorkspaceLiveChecks(); }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static BitmapSource WorkspaceBitmap(byte red, byte blue) => BitmapSource.Create(
        2, 2, 96, 96, PixelFormats.Bgra32, null,
        new byte[] { blue, 0, red, 255, blue, 0, red, 255, blue, 0, red, 255, blue, 0, red, 255 }, 8);

    private static async Task WorkspaceLiveChecks()
    {
        var directory = AppContext.BaseDirectory;
        var mainStore = new StateStore();
        var main = NewState();
        main.Theme = "dark"; main.UiLanguage = "en-US";
        main.McpEnabled = main.McpAllowFullWrites = true;
        main.Workspaces = new() { Enabled = true, Count = 5 };
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
            controller.SaveNow(); // deliberately leave an asynchronous ordinary write queued
            Require(controller.State.Papers.Single(p => p.Id == "note-a").Content == expectedDraft,
                "ordinary save did not commit the fixture editor");
            Require(await controller.CycleWorkspaceAsync(), "first cycle failed");
            Require(controller.WorkspaceNumber == 2 && oldWindow.IsClosed && !ReferenceEquals(oldWindow, windows["note-a"]),
                "workspace switch reused the old paper window");
            Require(controller.State.Theme == "dark" && controller.State.McpEnabled &&
                UiLanguages.EffectiveUiCulture.Name == "en-US", "default inheritance missing");
            var savedDraft = Str(J(File.ReadAllText(mainStore.FilePath))["papers"]![0]!["content"]);
            Require(savedDraft == expectedDraft,
                "pending editor text was lost on departure: " + JsonSerializer.Serialize(savedDraft));
            Throws<McpApiException>(() => previousMcp.Execute(JsonSerializer.SerializeToElement(new { method = "list_papers" })));
            var currentMcp = new McpCommandService(controller, controller.PaperCommands);
            Require(currentMcp.Execute(JsonSerializer.SerializeToElement(new { method = "list_papers" })) != null,
                "new-workspace MCP adapter could not read current papers");
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
                workspaceStatus.FollowsMainSettings && !workspaceStatus.IsReloading,
                "status does not identify the actual reload target");
            Require(!controller.PublicSettings.Get("appearance.theme").Writable, "inherited setting advertised writable");
            Throws<PaperSettingsException>(() => controller.PublicSettings.Set("appearance.theme", JsonSerializer.SerializeToElement("light")));
            controller.SaveNow(sync: true);
            Require(Str(J(File.ReadAllText(store2.FilePath))["theme"]) == "light", "inherited settings saved over dormant settings");

            var independent = controller.WorkspaceOptions.Normalized(); independent.FollowMainSettings[2] = false;
            Require(await controller.ChangeWorkspaceConfigurationAsync(independent), "unfollow failed");
            Require(controller.State.Theme == "light" && !controller.State.McpEnabled &&
                UiLanguages.EffectiveUiCulture.Name == "ja-JP" && controller.PublicSettings.Get("appearance.theme").Writable,
                "independent software settings/language did not activate");
            var following = controller.WorkspaceOptions.Normalized(); following.FollowMainSettings[2] = true;
            Require(await controller.ChangeWorkspaceConfigurationAsync(following), "refollow failed");
            Require(controller.State.Theme == "dark" && controller.State.McpEnabled, "refollow did not reapply main settings");
            Require(await controller.SwitchWorkspaceAsync(1), "return to main failed");
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

            // Both settings and tray choose an explicit slot, unlike the paper's cycle button.
            var workspacePage = (StackPanel)typeof(AppController)
                .GetMethod("BuildWorkspaceSettingsPage", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!
                .Invoke(controller, null)!;
            var workspaceCombo = workspacePage.Children.OfType<ComboBox>().Single();
            Require(workspaceCombo.Items.Count == 5 && workspaceCombo.SelectedIndex == 0 &&
                AutomationProperties.GetAutomationId(workspaceCombo) == "WorkspaceSelect",
                "settings workspace dropdown did not reflect the active slot");
            workspaceCombo.SelectedIndex = 2;
            await Until(() => controller.WorkspaceNumber == 3 && !controller.IsWorkspaceSwitching,
                "settings workspace dropdown did not select slot 3");
            Require(await controller.SwitchWorkspaceAsync(1), "return after settings selection failed");

            var trayMenu = controller.CreateTrayMenu();
            controller.RebuildTrayMenu(trayMenu);
            var workspaceMenu = trayMenu.Items.OfType<MenuItem>().Single(item =>
                item.Items.Count == controller.WorkspaceOptions.Count &&
                item.Header?.ToString()?.Contains("workspace", StringComparison.OrdinalIgnoreCase) == true);
            Require(workspaceMenu.Items.Count == 5 &&
                ((MenuItem)workspaceMenu.Items[0]).Header?.ToString()?.Contains("✓") == true,
                "tray workspace submenu did not mark the current slot");
            ((MenuItem)workspaceMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => controller.WorkspaceNumber == 4 && !controller.IsWorkspaceSwitching,
                "tray workspace submenu did not select slot 4");
            Require(await controller.SwitchWorkspaceAsync(1), "return after tray selection failed");

            File.WriteAllText(Path.Combine(directory, "data3.json"), "{invalid target");
            Require(!await controller.SwitchWorkspaceAsync(3) && controller.WorkspaceNumber == 1,
                "invalid target displaced current workspace");
            File.Delete(Path.Combine(directory, "data3.json"));
            Require(await controller.SwitchWorkspaceAsync(5), "new fifth workspace failed");
            Require(controller.State.Papers.Count == 1 && controller.State.Papers[0].Content.Length == 0,
                "new fifth workspace duplicated main content");
            var fifthPath = Path.Combine(directory, "data5.json");
            var fifthId = controller.State.Papers[0].Id;
            var reduced = controller.WorkspaceOptions.Normalized(); reduced.Count = 3;
            Require(await controller.ChangeWorkspaceConfigurationAsync(reduced) && controller.WorkspaceNumber == 1,
                "reducing count did not return an excluded active slot to main");
            Require(File.Exists(fifthPath) && Str(J(File.ReadAllText(fifthPath))["papers"]![0]!["id"]) == fifthId,
                "reducing count removed or changed fifth workspace data");
            Require(await controller.CycleWorkspaceAsync() && controller.WorkspaceNumber == 2, "cycle to2 failed");
            Require(await controller.CycleWorkspaceAsync() && controller.WorkspaceNumber == 3, "cycle to3 failed");
            Require(await controller.CycleWorkspaceAsync() && controller.WorkspaceNumber == 1, "cycle did not wrap at count");
            var disabled = controller.WorkspaceOptions.Normalized(); disabled.Enabled = false;
            Require(await controller.ChangeWorkspaceConfigurationAsync(disabled), "disable failed");
            foreach (var window in windows.Values)
            {
                window.EnsureShellBuilt();
                var button = Field<Button>(window, "_workspaceButton");
                Require(button.Visibility == Visibility.Collapsed && AutomationProperties.GetAutomationId(button) == "WorkspaceCycle",
                    "disabled workspace button still visible");
            }
            Require(!File.Exists(Path.Combine(directory, "workspaces.json")), "separate catalog was created");
            Console.WriteLine("PASS workspace WPF: cycle, editor flush, ID collision, inheritance, language, plugin data, failures and slot retention");
        }
        finally { controller.Dispose(); }
    }
}
