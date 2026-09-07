using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class Program
{
    private static readonly PaperBodyTheme Light = new(false, "#FFF8E6", "#202020", "#707070",
        "#B07A31", "#807050", "Segoe UI", 1);

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new (string Name, Action Run)[]
        {
            ("note-assets-permissions-ownership-detached-bytes-limits", AssetReads),
            ("paper-action-order-filter-revision-and-revocation", PaperActions),
            ("paper-action-adapter-permissions-and-target-checks", PaperActionAdapter),
            ("anchor-lease-expiry-movement-hidden-source", Anchors),
            ("window-single-instance-factory-failure-and-disposal", Windows),
            ("popup-dropdown-escape-and-owner-movement", Popups),
            ("context-menu-anchor-survives-menu-dismissal", MenuAnchor),
            ("surface-factories-marshal-worker-to-ui", WorkerFactory),
            ("surface-size-cap-and-content-ownership", SurfaceLimits),
            ("web-entry-containment-and-data-limits", WebEntry),
            ("web-runtime-rejects-stale-ui-document", WebDocumentLease),
            ("web-surface-bridge-request-response-behavior", WebBridge)
        };
        var failed = 0;
        foreach (var (name, run) in checks)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
            finally
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
                Pump();
            }
        }
        Console.WriteLine($"Plugin API checks: {checks.Length - failed}/{checks.Length} passed.");
        Application.Current.Shutdown();
        return failed == 0 ? 0 : 1;
    }

    private static void AssetReads()
    {
        using var temp = new Temp();
        using var store = new NoteImageStore(Path.Combine(temp.Path, "assets.lmdb"));
        store.Load();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] {0,0,255,255,0,255,0,255,255,0,0,255,0,0,0,255}, 8);
        bitmap.Freeze();
        var asset = store.ImportBitmapSource("note-a", bitmap);
        var controller = Controller(new AppState { Papers =
        [
            new() { Id = "note-a", Type = PaperTypes.Note, BodyProviderId = PaperBodyProviderIds.Markdown, Title = "A" },
            new() { Id = "note-b", Type = PaperTypes.Note, BodyProviderId = PaperBodyProviderIds.Markdown, Title = "B" },
            new() { Id = "foreign", Type = PaperTypes.Note, BodyProviderId = "other.plugin", Title = "Foreign" },
            new() { Id = "todo", Type = PaperTypes.Todo, Title = "Todo" }
        ] }, store);
        using var denied = BodyApi(controller, []);
        Error("permission_denied", () => denied.ReadImage("note-a", asset.Id));
        using var api = BodyApi(controller, [PaperTodoPermissionNames.NotesRead]);
        var image = api.ReadImage("note-a", asset.Id);
        Assert(image.Mime.StartsWith("image/") && image.Bytes.Length > 0, "Encoded bytes and MIME missing.");
        var first = image.Bytes[0];
        image.Bytes[0] ^= 255;
        Assert(api.ReadImage("note-a", asset.Id).Bytes[0] == first, "An exported array modified the image store.");
        Error("asset_not_found", () => api.ReadImage("note-b", asset.Id));
        Error("asset_not_found", () => api.ReadImage("note-a", "missing"));
        Error("note_content_unavailable", () => api.ReadImage("foreign", asset.Id));
        Error("wrong_paper_type", () => api.ReadImage("todo", asset.Id));
        Error("paper_not_found", () => api.ReadImage("deleted", asset.Id));
        Assert(!store.TryReadOwnedImage("note-a", asset.Id, 1, out _, out var bytes, out var tooLarge) &&
            tooLarge && bytes.Length == 0, "The cap must reject before returning bytes.");
        Assert(!store.TryReadOwnedImage("note-b", asset.Id, 1, out _, out _, out tooLarge) && !tooLarge,
            "Wrong-owner reads disclose size metadata.");
        var originalLength = asset.ByteLength;
        asset.ByteLength = IPaperNoteAssetsApi.MaximumImageBytes + 1;
        Error("asset_too_large", () => api.ReadImage("note-a", asset.Id));
        asset.ByteLength = originalLength;
        api.Dispose();
        Error("session_closed", () => api.ReadImage("note-a", asset.Id));
    }

    private static void PaperActions()
    {
        var registry = new PluginPaperActionRegistry();
        var owner = Guid.NewGuid();
        var paper = new PaperSnapshot("paper", PaperTypes.Note, "Current", true, false, false, "builtin.markdown");
        var active = true;
        var invoked = 0;
        registry.Set(owner, "a.plugin", paper.Id, PluginContributionPolicy.NormalizePaperActions(
        [
            Action("low", 1), Action("high", 9), Action("hidden", 99) with { Visible = false },
            Action("other", 99) with { BodyProviderId = "other.plugin" }
        ]), () => active, _ => invoked++);
        var rows = registry.Get(paper, PaperActionPlacement.TopBar);
        Assert(rows.Select(x => x.Action.Id).SequenceEqual(new[] { "high", "low" }), "Priority/provider/visibility filtering failed.");
        Assert(registry.TryResolve(rows[0], paper, PaperActionPlacement.TopBar, out var invoke), "Current action not invocable.");
        invoke!(new PaperActionInvocation("high", paper, PaperActionPlacement.TopBar, null));
        Assert(invoked == 1, "Click handler not dispatched.");
        registry.Set(owner, "a.plugin", paper.Id, [Action("high", 10)], () => active, _ => invoked++);
        Assert(!registry.TryResolve(rows[0], paper, PaperActionPlacement.TopBar, out _), "A replaced registration accepted a stale click.");
        var current = registry.Get(paper, PaperActionPlacement.TopBar)[0];
        Assert(!registry.TryResolve(current, paper with { Id = "other" }, PaperActionPlacement.TopBar, out _), "Wrong paper accepted.");
        active = false;
        Assert(registry.Get(paper, PaperActionPlacement.TopBar).Length == 0 &&
            !registry.TryResolve(current, paper, PaperActionPlacement.TopBar, out _), "Inactive Runtime retained actions.");
        active = true;
        registry.Clear(owner, paper.Id);
        Assert(!registry.TryResolve(current, paper, PaperActionPlacement.TopBar, out _), "Cleared action still executes.");
        registry.Set(owner, "a.plugin", paper.Id, [Action("disabled", 1) with { Enabled = false }], () => true, _ => { });
        Assert(!registry.TryResolve(registry.Get(paper, PaperActionPlacement.TopBar)[0], paper,
            PaperActionPlacement.TopBar, out _), "Disabled action executed.");
        registry.RemovePaper(paper.Id);
        Assert(registry.Get(paper, PaperActionPlacement.TopBar).Length == 0, "Deleted paper retained actions.");
        registry.RemoveOwner(owner);
        Error("invalid_paper_action_id", () => PluginContributionPolicy.NormalizePaperActions([Action("same", 1), Action("same", 2)]));
        Error("invalid_paper_action_placement", () => PluginContributionPolicy.NormalizePaperActions([Action("bad", 1) with { Placement = (PaperActionPlacement)8 }]));
        Error("too_many_paper_actions", () => PluginContributionPolicy.NormalizePaperActions(Enumerable.Range(0, 33).Select(i => Action($"a{i}", i)).ToArray()));
    }

    private static PaperAction Action(string id, int priority) => new()
    {
        Id = id, Text = id, Icon = PaperTopBarIcon.Character("+"), Priority = priority,
        Placement = PaperActionPlacement.TopBar | PaperActionPlacement.ContextMenu
    };

    private static void PaperActionAdapter()
    {
        var controller = Controller(new AppState { Papers = [new() { Id = "p", Title = "Test", Type = PaperTypes.Note }] });
        using var denied = new PaperPluginRuntimeWorkspaceApi(controller, "denied.plugin", [], () => true);
        var deniedActions = (IPaperPluginPaperActions)denied;
        deniedActions.SetActionHandler(_ => { });
        Error("permission_denied", () => deniedActions.SetActions("p", [Action("a", 1)]));
        using var allowed = new PaperPluginRuntimeWorkspaceApi(controller, "allowed.plugin", [PaperTodoPermissionNames.PapersRead], () => true);
        var actions = (IPaperPluginPaperActions)allowed;
        Error("paper_action_handler_missing", () => actions.SetActions("p", [Action("a", 1)]));
        actions.SetActionHandler(_ => { });
        Error("paper_not_found", () => actions.SetActions("missing", [Action("a", 1)]));
        actions.SetActions("p", [Action("a", 1)]);
        Assert(controller.GetPluginPaperActions("p", PaperActionPlacement.ContextMenu).Count == 1, "No registered paper action.");
        allowed.Dispose();
        Assert(controller.GetPluginPaperActions("p", PaperActionPlacement.ContextMenu).Count == 0, "Runtime teardown retained an action.");
        Error("runtime_closed", () => actions.SetActions("p", [Action("a", 1)]));
    }

    private static void Anchors()
    {
        var now = 1L;
        var store = new PluginUiAnchorStore(() => now);
        var lease = Guid.NewGuid();
        var (window, button) = Owner();
        var anchor = store.Capture(lease, "p", button, window, false)!;
        Assert(anchor != null, "No visible button anchor.");
        _ = store.Resolve(lease, anchor);
        Error("anchor_unavailable", () => store.Resolve(Guid.NewGuid(), anchor));
        now += 30_001;
        Error("anchor_unavailable", () => store.Resolve(lease, anchor));
        anchor = store.Capture(lease, "p", button, window, false)!;
        window.Left += 20; Pump();
        Error("anchor_unavailable", () => store.Resolve(lease, anchor));
        anchor = store.Capture(lease, "p", button, window, false)!;
        button.Visibility = Visibility.Collapsed; Pump();
        Error("anchor_unavailable", () => store.Resolve(lease, anchor));
        button.Visibility = Visibility.Visible; Pump();
        anchor = store.Capture(lease, "p", button, window, false)!;
        store.RemoveOwner(lease);
        Error("anchor_unavailable", () => store.Resolve(lease, anchor));
        window.Close();
    }

    private static void Windows()
    {
        var (owner, _) = Owner();
        using var host = Host(new PluginUiAnchorStore(), Guid.NewGuid(), owner);
        var content = new Content();
        var first = host.OpenWindow(new() { Id = "one", Title = "Plugin check" }, _ => content);
        var second = host.OpenWindow(new() { Id = "one" }, _ => throw new Exception("Factory ran twice."));
        Assert(ReferenceEquals(first, second) && first.IsOpen, "Window ids are not single-instance.");
        host.RefreshTheme();
        Assert(content.ThemeCount == 2, "Initial and explicit theme refresh were not delivered.");
        try { host.OpenWindow(new() { Id = "failure" }, _ => throw new InvalidOperationException("factory")); }
        catch (InvalidOperationException ex) when (ex.Message == "factory") { }
        var recovered = host.OpenWindow(new() { Id = "failure" }, _ => new Content());
        recovered.Close();
        var canceled = new Content();
        Error("surface_closed", () => host.OpenWindow(new() { Id = "cancel" }, context => { context.Close(); return canceled; }));
        Assert(canceled.DisposeCount == 1, "Canceled factory content was leaked.");
        owner.Close(); Pump();
        Assert(!first.IsOpen && content.DisposeCount == 1, "Owner close did not dispose accepted content exactly once.");
        first.Close(); host.Dispose();
        Assert(content.DisposeCount == 1, "Closing a stale handle disposed content twice.");
        Error("surface_owner_closed", () => host.OpenWindow(new() { Id = "late" }, _ => new Content()));
    }

    private static void Popups()
    {
        var (owner, button) = Owner();
        var store = new PluginUiAnchorStore();
        var lease = Guid.NewGuid();
        using var host = Host(store, lease, owner);
        var combo = new ComboBox { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0 };
        var content = new Content(combo);
        var handle = host.OpenPopup(store.Capture(lease, "p", button, owner, false)!, new() { Id = "popup" }, context =>
        {
            context.Controls.ApplySelectStyle(combo, 12);
            return content;
        });
        Pump();
        Assert(handle.IsOpen, "Popup closed while opening.");
        combo.Focus(); combo.IsDropDownOpen = true; Pump();
        Assert(handle.IsOpen && combo.IsDropDownOpen, "A styled dropdown incorrectly dismissed its outer shell.");
        Escape(combo); Pump();
        Assert(handle.IsOpen && !combo.IsDropDownOpen, "First Escape should close only the inner dropdown.");
        Escape(combo); Pump();
        Assert(!handle.IsOpen && content.DisposeCount == 1, "Second Escape did not close the popup.");
        var moving = host.OpenPopup(store.Capture(lease, "p", button, owner, false)!, new() { Id = "motion" }, _ => new Content());
        owner.Left += 10; Pump();
        Assert(!moving.IsOpen, "Moving the source owner left a stale popup.");
        owner.Close();
    }

    private static void MenuAnchor()
    {
        var (owner, button) = Owner();
        var menu = new ContextMenu { PlacementTarget = button };
        var item = new MenuItem { Header = "Open panel" };
        menu.Items.Add(item); menu.IsOpen = true; Pump();
        var store = new PluginUiAnchorStore();
        var lease = Guid.NewGuid();
        var anchor = store.Capture(lease, "p", item, owner, true)!;
        menu.IsOpen = false; Pump();
        using var host = Host(store, lease, owner);
        var handle = host.OpenPopup(anchor, new() { Id = "menu-panel" }, _ => new Content());
        Assert(handle.IsOpen, "An asynchronous menu action lost its anchor when the menu closed.");
        handle.Close(); owner.Close();
    }

    private static void WorkerFactory()
    {
        using var host = Host(new PluginUiAnchorStore(), Guid.NewGuid(), null);
        var createdOnUi = false;
        var task = Task.Run(() => host.OpenWindow(new() { Id = "worker" }, _ =>
        {
            createdOnUi = Application.Current.Dispatcher.CheckAccess();
            return new Content();
        }));
        PumpUntil(() => task.IsCompleted);
        var handle = task.GetAwaiter().GetResult();
        Assert(createdOnUi && handle.IsOpen, "A worker-thread request created WPF controls on the worker.");
        handle.Close();
    }

    private static void SurfaceLimits()
    {
        Error("invalid_surface_size", () => PluginSurfaceHost.Validate("a", double.NaN, 300));
        Error("invalid_surface_size", () => PluginSurfaceHost.Validate("a", 300, 0));
        Error("invalid_surface_id", () => PluginSurfaceHost.Validate("../a", 300, 200));
        using var host = Host(new PluginUiAnchorStore(), Guid.NewGuid(), null);
        var shared = new Content();
        var first = host.OpenWindow(new() { Id = "shared" }, _ => shared);
        Error("surface_content_in_use", () => host.OpenWindow(new() { Id = "borrowed" }, _ => shared));
        Assert(shared.DisposeCount == 0 && first.IsOpen, "Rejecting reused content damaged the original surface.");
        for (var i = 0; i < 7; i++) host.OpenWindow(new() { Id = $"w{i}" }, _ => new Content());
        Error("too_many_surfaces", () => host.OpenWindow(new() { Id = "ninth" }, _ => new Content()));
        host.CloseAll();
        Assert(!first.IsOpen && shared.DisposeCount == 1, "CloseAll leaked an accepted view.");
        var replacement = host.OpenWindow(new() { Id = "shared" }, _ => new Content());
        first.Close();
        Assert(replacement.IsOpen, "A stale handle closed a newer surface with the same id.");
    }

    private static void WebEntry()
    {
        using var temp = new Temp();
        var root = Path.Combine(temp.Path, "web"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "panel.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(temp.Path, "outside.html"), "<!doctype html>");
        Assert(WebPluginSurfaceRequests.ResolveEntry(root, "panel.html").EndsWith("panel.html"), "Local entry rejected.");
        foreach (var path in new[] { "../outside.html", "https://example.com/a.html", Path.Combine(temp.Path, "outside.html"), "missing.html" })
            Error("invalid_surface_entry", () => WebPluginSurfaceRequests.ResolveEntry(root, path));
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new string('字', 30_000)));
        Error("surface_message_too_large", () => WebPluginSurfaceRequests.ValidateMessage(data.RootElement));
    }

    private static void WebDocumentLease()
    {
        using var request = JsonDocument.Parse("""{"uiToken":"old","payload":{"method":"surfaces.openWindow"}}""");
        var root = request.RootElement;
        var payload = root.GetProperty("payload");
        Assert(!WebPluginRuntime.AcceptsExtensionRequest(root, payload, "new"), "Old Web document opened a new shell.");
        Assert(WebPluginRuntime.AcceptsExtensionRequest(root, payload, "old"), "Current Web token rejected.");
        Assert(!WebPluginRuntime.AcceptsExtensionRequest(root, payload, null), "A revoked Web document retained UI access.");
        using var legacy = JsonDocument.Parse("""{"method":"papers.list"}""");
        Assert(WebPluginRuntime.AcceptsExtensionRequest(root, legacy.RootElement, null), "Legacy Workspace transport changed.");
    }

    private static void WebBridge()
    {
        using var temp = new Temp();
        var bridgePath = Path.Combine(temp.Path, "bridge.js");
        File.WriteAllText(bridgePath, WebPluginSurfaceContent.BridgeScript("https://surface.test"), new UTF8Encoding(false));
        var harness = Path.Combine(temp.Path, "test.cjs");
        File.WriteAllText(harness, """
            const vm = require('node:vm'), fs = require('node:fs'), assert = require('node:assert/strict');
            const posted = [], handlers = [], styles = new Map();
            globalThis.location = {origin:'https://surface.test'};
            globalThis.chrome = {webview:{postMessage:x=>posted.push(x), addEventListener:(_,fn)=>handlers.push(fn)}};
            globalThis.document = {documentElement:{style:{setProperty:(k,v)=>styles.set(k,v)}}};
            globalThis.window = globalThis; window.top=window; window.addEventListener=()=>{};
            vm.runInThisContext(fs.readFileSync(process.argv[2], 'utf8'));
            const deliver = data => handlers.forEach(fn=>fn({data}));
            (async()=>{
              const promise = papertodo.noteAssets.readImage('note','image');
              assert.equal(posted.length,0);
              deliver({type:'initialize',token:'t',theme:{paperColor:'#fff',isDark:false},data:{paperId:'note'}});
              await Promise.resolve();
              const message = posted.shift(); assert.equal(message.token,'t');
              assert.equal(message.method,'noteAssets.readImage'); assert.equal(message.params.imageId,'image');
              deliver({type:'response',requestId:message.requestId,ok:true,result:{mime:'image/png',bytes:'AA=='}});
              assert.equal((await promise).bytes,'AA==');
              const denied = papertodo.workspace.request('papers.list'); await Promise.resolve();
              const second=posted.shift();
              deliver({type:'response',requestId:second.requestId,ok:false,error:{code:'permission_denied',message:'denied'}});
              await assert.rejects(denied, e=>e.code==='permission_denied');
              assert.equal(styles.get('--paper-background'),'#fff');
              papertodo.surface.close(); assert.equal(posted.pop().method,'surface.close');
              assert.equal(papertodo.surfaces,undefined);
            })().catch(e=>{console.error(e);process.exitCode=1;});
            """, new UTF8Encoding(false));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(harness); start.ArgumentList.Add(bridgePath);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new Exception("Node bridge check timed out."); }
        Assert(process.ExitCode == 0, error.GetAwaiter().GetResult());
    }

    private static (Window Window, Button Button) Owner()
    {
        var button = new Button { Content = "Anchor", Width = 100, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var window = new Window { Content = new Grid { Children = { button } }, Width = 400, Height = 250,
            Left = 100, Top = 100, ShowInTaskbar = false };
        window.Show(); window.Activate(); window.UpdateLayout(); Pump();
        return (window, button);
    }
    private static PluginSurfaceHost Host(PluginUiAnchorStore anchors, Guid lease, Window? owner) =>
        new(lease, "test.plugin", () => true, anchors, () => Light, () => owner);
    private static PaperBodyPluginHostApi BodyApi(AppController controller, string[] permissions) =>
        new(controller, controller.PaperCommands, "note-a", "test.plugin", permissions, () => true, () => true);
    private static AppController Controller(AppState state, NoteImageStore? images = null)
    {
        // Avoid AppController's real startup (user data, tray and timers). Initialize only inert
        // collection fields used by these command/facade paths; no alternate production writer.
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        foreach (var field in typeof(AppController).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var type = field.FieldType;
            if (!type.IsGenericType) continue;
            var generic = type.GetGenericTypeDefinition();
            if (generic == typeof(Dictionary<,>) || generic == typeof(List<>) || generic == typeof(HashSet<>))
                field.SetValue(controller, Activator.CreateInstance(type));
        }
        typeof(AppController).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, state);
        if (images != null) typeof(AppController).GetField("_imageStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, images);
        return controller;
    }
    private static void Escape(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(target)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.ElapsedMilliseconds > 15_000) throw new TimeoutException(); Pump(); Thread.Sleep(1); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Error(string code, Action action)
    {
        try { action(); }
        catch (PaperTodoPluginException ex) when (ex.Code == code) { return; }
        throw new Exception($"Expected plugin error: {code}");
    }
    private sealed class Content(FrameworkElement? view = null) : IPaperPluginSurfaceContent
    {
        public FrameworkElement View { get; } = view ?? new TextBlock { Text = "Plugin content" };
        public int DisposeCount;
        public int ThemeCount;
        public void OnThemeChanged(PaperBodyTheme theme) => ThemeCount++;
        public void Dispose() => DisposeCount++;
    }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaperTodo.PluginApiChecks-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
