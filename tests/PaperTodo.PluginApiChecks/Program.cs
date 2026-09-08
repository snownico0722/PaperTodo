using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static readonly PaperBodyTheme Light = new(false, "#FFF8E6", "#202020", "#707070",
        "#B07A31", "#807050", "Segoe UI", 1);

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new (string Name, Action Run)[]
        {
            ("note-image-permissions-ownership-independent-bytes-limits", AssetReads),
            ("plain-menu-order-replacement-and-revocation", PaperActions),
            ("menu-adapter-permissions-and-cleanup", PaperActionAdapter),
            ("real-menu-dismissal-then-popup", MenuPopup),
            ("popup-internal-focus-source-motion-and-deactivation", PopupInteraction),
            ("popup-factory-close-replacement-and-disposal", PopupLifetime),
            ("popup-worker-dispatch-and-session-cleanup", PopupFacade),
            ("one-shot-screen-placement-and-size-validation", Placement),
            ("existing-topbar-constructor-compatible", TopBarCompatibility),
            ("web-entry-containment-and-message-limits", WebEntry),
            ("web-document-rejects-stale-menu-popup-requests", WebDocument),
            ("shared-web-process-recovery-classification", ProcessFailures),
            ("popup-js-bridge-read-only-images-and-messages", WebBridge)
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

    private static PaperAction Item(string id) => new() { Id = id, Text = id };

    private static void PaperActions()
    {
        var registry = new PluginPaperActionRegistry();
        var owner = Guid.NewGuid();
        var paper = new PaperSnapshot("p", PaperTypes.Note, "Current", true, false, false, "builtin.markdown");
        var active = true;
        var invoked = 0;
        var normalized = PluginContributionPolicy.NormalizePaperActions([Item("second"), Item("first")]);
        registry.Set(owner, "a.plugin", paper.Id, normalized, () => active, _ => invoked++);
        var rows = registry.Get(paper);
        Assert(rows.Select(x => x.Action.Id).SequenceEqual(new[] { "second", "first" }), "Declaration order changed.");
        Assert(registry.TryResolve(rows[0], paper, out var handler), "Text-only action not invocable.");
        handler!(new(rows[0].Action.Id, paper, new(120, 140)));
        Assert(invoked == 1, "Callback not dispatched.");
        registry.Set(owner, "a.plugin", paper.Id, [Item("second")], () => active, _ => invoked++);
        Assert(!registry.TryResolve(rows[0], paper, out _), "Replaced registration accepted an old menu click.");
        var current = registry.Get(paper)[0];
        Assert(!registry.TryResolve(current, paper with { Id = "other" }, out _), "Wrong paper accepted.");
        active = false;
        Assert(registry.Get(paper).Length == 0 && !registry.TryResolve(current, paper, out _), "Inactive runtime retained action.");
        active = true;
        registry.Clear(owner, paper.Id);
        Assert(!registry.TryResolve(current, paper, out _), "Clear did not revoke menu callback.");
        registry.Set(owner, "a.plugin", paper.Id, normalized, () => true, _ => { });
        registry.RemovePaper(paper.Id);
        Assert(registry.Get(paper).Length == 0, "Deleted paper retained actions.");
        registry.RemoveOwner(owner);
        Error("invalid_paper_action_id", () => PluginContributionPolicy.NormalizePaperActions([Item("same"), Item("same")]));
        Error("invalid_paper_action_text", () => PluginContributionPolicy.NormalizePaperActions([Item("empty") with { Text = " " }]));
        Error("too_many_paper_actions", () => PluginContributionPolicy.NormalizePaperActions(Enumerable.Range(0, 33).Select(i => Item($"a{i}")).ToArray()));
    }

    private static void PaperActionAdapter()
    {
        var controller = Controller(new AppState { Papers = [new() { Id = "p", Title = "Test", Type = PaperTypes.Note }] });
        using var denied = new PaperPluginRuntimeWorkspaceApi(controller, "denied.plugin", [], () => true);
        var deniedActions = (IPaperPluginPaperActions)denied;
        deniedActions.SetActionHandler(_ => { });
        Error("permission_denied", () => deniedActions.SetActions("p", [Item("a")]));
        using var allowed = new PaperPluginRuntimeWorkspaceApi(controller, "allowed.plugin", [PaperTodoPermissionNames.PapersRead], () => true);
        var actions = (IPaperPluginPaperActions)allowed;
        Error("paper_action_handler_missing", () => actions.SetActions("p", [Item("a")]));
        actions.SetActionHandler(_ => { });
        Error("paper_not_found", () => actions.SetActions("missing", [Item("a")]));
        actions.SetActions("p", [Item("a")]);
        Assert(controller.GetPluginPaperActions("p").Count == 1, "Action not registered.");
        allowed.Dispose();
        Assert(controller.GetPluginPaperActions("p").Count == 0, "Runtime teardown retained action.");
        Error("runtime_closed", () => actions.SetActions("p", [Item("a")]));
    }

    private static Window Source()
    {
        var window = new Window { Content = new Button { Content = "Source" }, Width = 350, Height = 200,
            Left = 100, Top = 100, ShowInTaskbar = false };
        window.Show(); window.Activate(); Pump();
        return window;
    }
    private static PluginPopupHost Host() => new(() => true, () => Light);
    private static Window PopupWindow(Content content)
    {
        PumpUntil(() => Window.GetWindow(content.View)?.IsVisible == true);
        return Window.GetWindow(content.View)!;
    }

    private static void MenuPopup()
    {
        var hidden = Source(); hidden.Hide();
        var visible = Source();
        var controller = Controller(new AppState { Papers = [new() { Id = "p", Type = PaperTypes.Note, Title = "Note" }] });
        using var runtime = new PaperPluginRuntimeWorkspaceApi(controller, "test.plugin", [PaperTodoPermissionNames.PapersRead], () => true);
        var actions = (IPaperPluginPaperActions)runtime;
        using var host = Host();
        var content = new Content(new TextBox { Text = "unsaved" });
        IPaperPluginPopup? popup = null;
        PaperActionInvocation? received = null;
        actions.SetActionHandler(action => { received = action; popup = host.Open(action.Position, new(), _ => content); });
        actions.SetActions("p", [Item("view-note")]);
        var menu = new ContextMenu { PlacementTarget = (UIElement)visible.Content };
        // Use the actual compact template and the production menu insertion/callback path.
        var style = (Style)typeof(PaperWindow).GetMethod("BuildCompactMenuItemStyle", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        menu.Resources[typeof(MenuItem)] = style;
        PaperWindow.AttachPluginPaperMenuActions(menu, controller, "p");
        menu.IsOpen = true; Pump();
        var item = menu.Items.OfType<MenuItem>().Single();
        Assert(item.Header?.ToString() == "view-note" && item.Icon == null && item.ToolTip == null, "Menu isn't plain text.");
        var peer = new MenuItemAutomationPeer(item);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        PumpUntil(() => received != null && !menu.IsOpen);
        var window = PopupWindow(content);
        Assert(popup!.IsOpen && window.IsActive, "Menu dismissal closed the newly opened popup.");
        Assert(received!.Paper.Id == "p" && !hidden.IsVisible, "Paper identity or hidden window was changed.");
        visible.Activate(); PumpUntil(() => !popup.IsOpen);
        Assert(content.DisposeCount == 1, "Menu popup not disposed on deactivation.");
    }

    private static void PopupInteraction()
    {
        var source = Source();
        using var host = Host();
        var first = new TextBox { Text = "unsaved" };
        var second = new TextBox { Text = "second" };
        var combo = new ComboBox { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0 };
        var content = new Content(new StackPanel { Children = { first, second, combo } });
        var popup = host.Open(new(550, 300), new(), context => { context.Controls.ApplySelectStyle(combo, 12); return content; });
        var window = PopupWindow(content);
        Assert(window.IsActive, "Popup did not activate.");
        first.Focus(); Pump(); second.Focus(); Pump();
        Assert(popup.IsOpen && first.Text == "unsaved", "Internal focus change closed content.");
        combo.Focus(); combo.IsDropDownOpen = true; Pump();
        Assert(popup.IsOpen && combo.IsDropDownOpen, "An internal dropdown deactivated the popup.");
        combo.IsDropDownOpen = false; Pump();
        Assert(WindowNative.TryGetWindowDeviceBounds(window, out var before), "No window bounds.");
        source.Left += 35; source.Width += 10; Pump();
        Assert(WindowNative.TryGetWindowDeviceBounds(window, out var after) && before == after && popup.IsOpen,
            "Popup followed or closed when its source moved programmatically.");
        host.RefreshTheme();
        Assert(content.ThemeCount >= 2, "Theme update not delivered.");
        source.Activate(); PumpUntil(() => !popup.IsOpen);
        Assert(content.DisposeCount == 1, "Deactivation did not dispose once.");

        var readOnly = new Content();
        var readOnlyPopup = host.Open(new(550, 300), new(), _ => readOnly);
        _ = PopupWindow(readOnly);
        Assert(((Border)VisualTreeHelper.GetParent(readOnly.View)).IsKeyboardFocusWithin, "Read-only content left focus in source.");
        readOnlyPopup.Close();
    }

    private static void PopupLifetime()
    {
        _ = Source();
        using var host = Host();
        var content = new Content();
        var closed = host.Open(new(500, 300), new(), context => { context.Close(); return content; });
        Assert(!closed.IsOpen && content.DisposeCount == 1, "Close during construction leaked or double-disposed content.");
        var first = new Content();
        var a = host.Open(new(500, 300), new(), _ => first);
        var second = new Content();
        var b = host.Open(new(500, 300), new(), _ => second);
        a.Close();
        Assert(!a.IsOpen && b.IsOpen && first.DisposeCount == 1, "Old handle affected replacement.");
        host.Dispose(); Pump();
        Assert(!b.IsOpen && second.DisposeCount == 1, "Dispose before deferred show leaked content.");
        Error("popup_host_closed", () => host.Open(new(0, 0), new(), _ => new Content()));
        using var other = Host();
        try { other.Open(new(500, 300), new(), _ => throw new InvalidOperationException("factory")); }
        catch (InvalidOperationException ex) when (ex.Message == "factory") { }
        var borrowed = new Content();
        var parent = new Grid(); parent.Children.Add(borrowed.View);
        Error("popup_content_in_use", () => other.Open(new(500, 300), new(), _ => borrowed));
        Assert(borrowed.DisposeCount == 0 && parent.Children.Contains(borrowed.View), "Rejected borrowed view was disposed or detached.");
    }

    private static void PopupFacade()
    {
        _ = Source();
        var controller = Controller(new AppState { Papers = [new() { Id = "note-a", Type = PaperTypes.Note }] });
        using var api = BodyApi(controller, []);
        var content = new Content();
        var thread = 0;
        var task = Task.Run(() => api.Open(new(550, 300), new(), _ => { thread = Environment.CurrentManagedThreadId; return content; }));
        PumpUntil(() => task.IsCompleted);
        var popup = task.GetAwaiter().GetResult();
        Assert(thread == Environment.CurrentManagedThreadId, "Worker factory did not marshal to UI.");
        _ = PopupWindow(content);
        api.Dispose();
        Assert(!popup.IsOpen && content.DisposeCount == 1, "Session disposal left its popup open.");
        Error("session_closed", () => api.Open(new(0, 0), new(), _ => new Content()));
    }

    private static void Placement()
    {
        var monitor = new MonitorGeometry("left", new(-1920, 0, 0, 1080), 1.5, 1.5);
        var bounds = PluginPopupHost.Place(new(-5, 1075), new() { Width = 320, Height = 240 }, monitor);
        Assert(bounds.Width == 480 && bounds.Height == 360 && bounds.Left >= -1920 && bounds.Right <= 0 && bounds.Bottom <= 1080,
            "Mixed-DPI/negative screen edge placement failed.");
        var huge = PluginPopupHost.Place(new(-1900, 5), new() { Width = 4096, Height = 4096 }, monitor);
        Assert(huge == monitor.WorkArea, "Oversize popup escaped work area.");
        Error("invalid_popup_position", () => PluginPopupHost.Validate(new(double.NaN, 0), new()));
        Error("invalid_popup_size", () => PluginPopupHost.Validate(new(0, 0), new() { Width = double.PositiveInfinity }));
        Error("invalid_popup_size", () => PluginPopupHost.Validate(new(0, 0), new() { Height = 1 }));
    }

    private static void TopBarCompatibility()
    {
        // This is the old five-argument public constructor and Deconstruct; it must still compile.
        var old = new PaperTopBarActionInvocation("a", PaperTopBarActionScope.Paper, "p", PaperTypes.Note, "provider");
        var (id, scope, paper, type, provider) = old;
        Assert(id == "a" && paper == "p" && old.Position == null, "Old invocation contract changed.");
        Assert((old with { Position = new(1, 2) }).Position == new PaperPopupPosition(1, 2), "New position not carried.");
    }

    private static void WebEntry()
    {
        using var temp = new Temp();
        var root = Path.Combine(temp.Path, "web"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "panel.html"), "<p>test</p>");
        File.WriteAllText(Path.Combine(temp.Path, "outside.html"), "<p>outside</p>");
        Assert(WebPluginPopupRequests.ResolveEntry(root, "panel.html") == Path.Combine(root, "panel.html"), "Local HTML rejected.");
        foreach (var bad in new[] { "../outside.html", "https://example.com/", "missing.html", "panel.html?x=1" })
            Error("invalid_popup_entry", () => WebPluginPopupRequests.ResolveEntry(root, bad));
        WebPluginPopupRequests.ValidateMessage(JsonSerializer.SerializeToElement(new { paperId = "p" }));
        Error("popup_message_too_large", () => WebPluginPopupRequests.ValidateMessage(JsonSerializer.SerializeToElement(new string('x', 65536))));
    }

    private static void WebDocument()
    {
        foreach (var method in new[] { "paperActions.set", "popups.open", "popups.close" })
        {
            var payload = JsonSerializer.SerializeToElement(new { method });
            Assert(!WebPluginRuntime.AcceptsExtensionRequest(JsonSerializer.SerializeToElement(new { uiToken = "old" }), payload, "new"), "Stale document accepted.");
            Assert(!WebPluginRuntime.AcceptsExtensionRequest(JsonSerializer.SerializeToElement(new { uiToken = "new" }), payload, null), "Navigation retained UI authority.");
            Assert(WebPluginRuntime.AcceptsExtensionRequest(JsonSerializer.SerializeToElement(new { uiToken = "new" }), payload, "new"), "Current document rejected.");
        }
    }

    private static void ProcessFailures()
    {
        foreach (var kind in new[] { CoreWebView2ProcessFailedKind.GpuProcessExited, CoreWebView2ProcessFailedKind.UtilityProcessExited,
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited })
        {
            Assert(WebPluginProcessFailurePolicy.Classify(kind) == WebPluginProcessFailurePolicy.Recovery.None,
                $"Recoverable process failure closes popup: {kind}");
            Assert(!WebPaperBodySession.ShouldResetExtensionUiOnProcessFailure(kind),
                $"Recoverable body process failure resets popup ownership: {kind}");
        }
        Assert(WebPluginProcessFailurePolicy.Classify(CoreWebView2ProcessFailedKind.BrowserProcessExited) == WebPluginProcessFailurePolicy.Recovery.Restart,
            "Browser failure not classified.");
        Assert(WebPluginProcessFailurePolicy.Classify(CoreWebView2ProcessFailedKind.RenderProcessExited) == WebPluginProcessFailurePolicy.Recovery.Reload,
            "Renderer failure not classified.");
        Assert(WebPaperBodySession.ShouldResetExtensionUiOnProcessFailure(CoreWebView2ProcessFailedKind.BrowserProcessExited) &&
            WebPaperBodySession.ShouldResetExtensionUiOnProcessFailure(CoreWebView2ProcessFailedKind.RenderProcessExited),
            "Fatal body process failure stopped resetting popup ownership.");
    }

    private static void WebBridge()
    {
        using var temp = new Temp();
        var bridge = Path.Combine(temp.Path, "bridge.js");
        File.WriteAllText(bridge, WebPluginPopupContent.BridgeScript("https://popup.test"), new UTF8Encoding(false));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bridge-check.cjs")); start.ArgumentList.Add(bridge);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new TimeoutException("Node bridge check timed out."); }
        Assert(process.ExitCode == 0, stderr.GetAwaiter().GetResult());
    }

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
    private static void Assert([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Error(string code, Action action)
    {
        try { action(); }
        catch (PaperTodoPluginException ex) when (ex.Code == code) { return; }
        throw new Exception($"Expected plugin error: {code}");
    }
    private sealed class Content(FrameworkElement? view = null) : IPaperPluginPopupContent
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
