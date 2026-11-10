using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;
using PaperTodo.Plugin.GoodLock;

internal static partial class InjectionSystemRunner
{
    private static async Task RunScenario(AppController c, string scenario)
    {
        switch (scenario)
        {
            case "timers": Timers(c); HoverProfiles(c); break;
            case "typography": await Typography(c); break;
            case "settings": Settings(c); break;
            case "menus": Menus(c); break;
            case "next-use": await NextUse(c); break;
            case "good-lock": GoodLock(c); break;
            case "conflict": Conflicts(c); break;
            case "lifetime": await Lifetime(c); break;
            case "restart-cancel": await RestartCancelled(c); break;
            case "restart": Restart(c); break;
            default: throw new ArgumentException(scenario);
        }
    }

    private static void Timers(AppController c)
    {
        c.SaveNow(sync: true);
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data.json"));
        foreach (var (key, field) in new[]
        {
            ("app.saveIdleMs", "_saveTimer"), ("app.saveForceMs", "_forceSaveTimer"),
            ("app.displayRefreshMs", "_displayMetricsRefreshTimer"),
            ("fullscreen.pollIntervalMs", "_topmostRefreshTimer"), ("fullscreen.eventDebounceMs", "_fullscreenEventDebounceTimer")
        })
        {
            var timer = Field<DispatcherTimer>(c, field);
            var original = timer.Interval;
            Set(c, (key, original.TotalMilliseconds + 37));
            Check(ReferenceEquals(timer, Field<DispatcherTimer>(c, field)) &&
                timer.Interval == original + TimeSpan.FromMilliseconds(37), key + " did not update the existing timer.");
            Check(c.ApplyUserInjection(null).ChangedKeys.Count == 0, key + " repeated apply was not a no-op.");
            Remove(c, key);
            Check(timer.Interval == original, key + " removal did not restore the actual timer.");
        }
        Check(data.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data.json"))),
            "Injection timer edits wrote ordinary workspace data.");
    }

    private static async Task Typography(AppController c)
    {
        var window = Windows(c)["note"];
        var box = Field<MarkdownPaperBodySession>(window, "_markdownBodySession").NoteBox!;
        var original = box.FontSize;
        box.Text = "pending typography draft";
        box.Select(2, 4);
        var document = box.Document;
        var preference = c.State.Zoom;
        Set(c, ("appearance.fontScale", 1.2));
        await Settle();
        Check(c.State.Zoom == preference && AppTypography.ScaleFactor == 1.2,
            "Font override replaced ordinary preferences or failed to reach typography.");
        Check(ReferenceEquals(box, Field<MarkdownPaperBodySession>(window, "_markdownBodySession").NoteBox) &&
            ReferenceEquals(document, box.Document) && box.Text == "pending typography draft" &&
            box.SelectionStart == 2 && box.SelectionLength == 4 && box.Document.UndoStack.CanUndo,
            "Hot typography replaced the editor, draft, selection or undo history.");
        Check(Math.Abs(box.FontSize - original * 1.2) < 0.11, "Typed scale changed but actual note font did not.");
        Remove(c, "appearance.fontScale");
        await Settle();
        Check(Math.Abs(box.FontSize - original) < 0.11 && c.State.Zoom == preference, "Actual font did not recover on removal.");
    }

    private static void Settings(AppController c)
    {
        foreach (var (key, setting, value) in new[]
        {
            ("appearance.fontScale", "appearance.font_scale", 1.1),
            ("paper.titleFontDip", "title.text_size", 17d),
            ("capsule.labelFontDip", "capsule.text_size", 15d),
            ("capsule.gap.finalDip", "capsule.gap", 9d)
        })
        {
            var original = c.PublicSettings.Get(setting);
            Check(original.Writable, setting + " fixture must initially be writable.");
            Set(c, (key, value));
            var locked = c.PublicSettings.Get(setting);
            Check(!locked.Writable && JsonElement.DeepEquals(locked.Value, original.Value), setting + " lock lost its preference.");
            Error<PaperSettingsException>(() => c.PublicSettings.Set(setting, original.Value), "setting_read_only");
            Remove(c, key);
            var restored = c.PublicSettings.Get(setting);
            Check(restored.Writable && JsonElement.DeepEquals(restored.Value, original.Value), setting + " did not unlock.");
        }
        var metrics = typeof(InjectionParameters).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(f => f.Name.StartsWith("TodoMetric", StringComparison.Ordinal))
            .Select(f => (InjectionParameter)f.GetValue(null)!).ToArray();
        Check(metrics.Length == 10, "Todo composite metric fixture changed; review all participating metrics.");
        Set(c, metrics.Take(9).Select(p => (p.Id, (object)24d)).ToArray());
        Check(c.PublicSettings.Get("todo.visual_size").Writable, "Partial Todo override locked the still-effective preset.");
        Set(c, (metrics[9].Id, 24d));
        Check(!c.PublicSettings.Get("todo.visual_size").Writable, "Complete Todo override left its preset writable.");
        Remove(c, metrics[0].Id);
        Check(c.PublicSettings.Get("todo.visual_size").Writable, "Removing one metric did not unlock the remaining calculation.");
        Remove(c, metrics.Skip(1).Select(p => p.Id).ToArray());

        var notes = new[] { "fontSize", "codeFontSize", "heading1FontSize", "heading2FontSize", "heading3FontSize" }
            .Select(n => "note.typography." + n).ToArray();
        Set(c, notes.Select(id => (id, (object)18d)).ToArray());
        Check(c.PublicSettings.Get("note.text_size").Writable, "Pending restart overrides locked the currently effective setting.");
        UserInjection.LoadStartup(UserInjection.FilePath);
        Check(!c.PublicSettings.Get("note.text_size").Writable, "Adopted complete note typography did not lock its preset.");
        Remove(c, notes[0]);
        Check(!c.PublicSettings.Get("note.text_size").Writable, "Pending restart removal prematurely unlocked the effective override.");
        UserInjection.LoadStartup(UserInjection.FilePath);
        Check(c.PublicSettings.Get("note.text_size").Writable, "Restarted partial note override did not restore preset control.");
    }

    private static void Menus(AppController c)
    {
        var window = Windows(c)["note"];
        c.State.TopBarPinnedCount = 0;
        c.State.HiddenTopBarShortcuts.Clear();
        window.UpdateTopBarNewPaperButtons();
        var paperMenu = (ContextMenu)Call(window, "BuildPaperContextMenu", false)!;
        var edgeMenu = (ContextMenu)Call(window, "BuildPaperContextMenu", true)!;
        bool Has(ContextMenu menu) => menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, Strings.Get("MenuNewTodoPaper")));
        void Open(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(Has(paperMenu) && Has(edgeMenu), "Both real menus must start with New Todo.");
        Set(c, ("menu.paper.newTodo", false));
        Open(paperMenu); Open(edgeMenu);
        Check(!Has(paperMenu) && Has(edgeMenu), "A paper-menu override leaked into the edge menu or missed an existing menu.");
        Set(c, ("menu.edge.newTodo", false));
        Open(edgeMenu); Check(!Has(edgeMenu), "Next-use edge override did not update the cached menu.");
        Remove(c, "menu.paper.newTodo", "menu.edge.newTodo");
        Open(paperMenu); Open(edgeMenu);
        Check(Has(paperMenu) && Has(edgeMenu), "Removed menu overrides did not restore commands.");
    }

    private static async Task NextUse(AppController c)
    {
        var note = c.State.Papers.Single(p => p.Id == "note");
        var originalSize = (note.Width, note.Height);
        Set(c, ("newPaper.note.widthDip", 420d), ("newPaper.note.heightDip", 300d),
            ("newPaper.todo.widthDip", 410d), ("newPaper.todo.heightDip", 290d));
        foreach (var type in new[] { PaperTypes.Note, PaperTypes.Todo })
        {
            var p = c.CreatePaper(type)!;
            await Settle();
            var w = Windows(c)[p.Id];
            Check(Math.Abs(w.ActualWidth - (type == PaperTypes.Note ? 420 : 410)) <= 1 &&
                Math.Abs(w.ActualHeight - (type == PaperTypes.Note ? 300 : 290)) <= 1,
                type + " new model did not reach its actual window.");
        }
        Check((note.Width, note.Height) == originalSize, "Next-use creation dimensions resized an existing note.");
        var box = Field<MarkdownPaperBodySession>(Windows(c)["note"], "_markdownBodySession").NoteBox!;
        var originalIndent = box.Options.IndentationSize;
        Set(c, ("note.editor.indentationSize", 7));
        var later = c.CreatePaper(PaperTypes.Note)!;
        await Settle();
        var laterBox = Field<MarkdownPaperBodySession>(Windows(c)[later.Id], "_markdownBodySession").NoteBox!;
        Check(box.Options.IndentationSize == originalIndent && laterBox.Options.IndentationSize == originalIndent,
            "Restart-only settings leaked into existing or newly constructed editors before restart.");
        Check(UserInjection.Read().Parameters.Single(p => p.Id == "note.editor.indentationSize").Status == "restart",
            "Restart-only editor setting has misleading status.");
    }

    private sealed class Controls : IPaperBodyControls { public void ApplySelectStyle(ComboBox comboBox, double fontSize) { } }
    private sealed class GoodLockView : IDisposable
    {
        private readonly PaperBodyPluginHostApi _host;
        public IPaperBodySession Session { get; }
        public FrameworkElement View => Session.View;
        public Window Window { get; }
        public string Draft { get; private set; }
        public GoodLockView(AppController c, string draft = "{}")
        {
            Draft = draft;
            _host = new PaperBodyPluginHostApi(c, c.PaperCommands, "note", "tests.good-lock-system",
                [PaperTodoPermissionNames.SettingsRead, PaperTodoPermissionNames.SettingsUpdate], () => true, () => true);
            Session = new GoodLockPlugin().Create(new PaperBodyContext
            {
                ProviderId = "official.good-lock.native", ApiVersion = "2.4", UiLanguage = "en-US",
                StateJson = draft, StateVersion = 1, TargetStateVersion = 1,
                Paper = new PaperBodyPaperContext { PaperId = "note", SetTitle = _ => { }, SetHeaderText = _ => { }, SetCapsulePresentation = _ => { } },
                Body = new PaperBodySurfaceContext
                {
                    Controls = new Controls(), Theme = new PaperBodyTheme(false, "#FFFFFF", "#222222", "#777777", "#337755", "#CCCCCC", "Segoe UI", 1),
                    SetInputClaims = _ => { }, MarkDirty = () => { }, OpenExternal = _ => { }, RequestReload = () => { }
                },
                Workspace = _host, Runtime = null!, SaveStateJson = json => Draft = json
            });
            Window = new Window { Width = 760, Height = 500, Content = View, ShowActivated = false };
            Window.Show(); Window.UpdateLayout();
        }
        public object[] Rows => ((ItemsControl)View.FindName("Rows")).Items.Cast<object>().ToArray();
        public object Row(string id) => Rows.Single(r => Property<string>(r, "Key") == id);
        public string Label(string name) => Property<string>(Property<object>(Session, "Labels"), name);
        public void Click(string name) => ((Button)View.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        public void Command(string id, string command) => Property<ICommand>(Row(id), command).Execute(null);
        public void Dispose() { Window.Content = null; Session.Dispose(); _host.Dispose(); Window.Close(); }
    }
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static bool RowState(GoodLockView view, string id, string label) => Property<string>(view.Row(id), "StateText") == view.Label(label);

    private static void GoodLock(AppController c)
    {
        const string initial = "{ /* preserved formatting */ \"appearance.fontScale\":1.1,\"todo.paste.maxItems\":100,\"note.editor.indentationSize\":7,\"future.keep\":{\"text\":\"中文\"}, }\n";
        File.WriteAllText(UserInjection.FilePath, initial);
        using var first = new GoodLockView(c);
        Check(first.Rows.Length == 4 && RowState(first, "appearance.fontScale", "Pending"), "Initial Good Lock rows or pending state incorrect.");
        first.Click("ApplyButton");
        Check(File.ReadAllText(UserInjection.FilePath) == initial && AppTypography.ScaleFactor == 1.1,
            "Good Lock external Apply rewrote formatting or missed the actual consumer.");
        Check(RowState(first, "appearance.fontScale", "Applied") && RowState(first, "note.editor.indentationSize", "RestartRequired"),
            "Good Lock applied/restart indicators disagree with runtime.");
        Check(!Property<ICommand>(first.Row("future.keep"), "OptimizeCommand").CanExecute(null), "Unknown key offered an invalid optimizer.");
        first.Command("todo.paste.maxItems", "ResetCommand");
        Check(RowState(first, "todo.paste.maxItems", "Pending") && InjectionParameters.TodoPasteLineLimit.Value == 100 &&
            File.ReadAllText(UserInjection.FilePath) == initial && first.Draft != "{}", "Reset did not remain an unapplied persistent draft.");
        using var restored = new GoodLockView(c, first.Draft);
        Check(Property<string>(restored.Row("todo.paste.maxItems"), "ValueText") == "—", "Recreated session lost its reset draft.");
        restored.Click("ApplyButton");
        Check(!UserInjection.Read().Overrides.ContainsKey("todo.paste.maxItems") && InjectionParameters.TodoPasteLineLimit.Value == 200 &&
            UserInjection.Read().Overrides.ContainsKey("future.keep") && restored.Draft == "{}", "Single reset did not preserve unrelated values or clear the draft.");
        var values = UserInjection.Read().Overrides.ToDictionary(p => p.Key, p => p.Value);
        values["animation.fastDurationScale"] = J(2d);
        File.WriteAllText(UserInjection.FilePath, JsonSerializer.Serialize(values));
        restored.Session.OnActivated();
        var before = File.ReadAllText(UserInjection.FilePath);
        restored.Command("animation.fastDurationScale", "OptimizeCommand");
        Check(File.ReadAllText(UserInjection.FilePath) == before && restored.Draft.Contains("animation.fastDurationScale"),
            "Suggestion was not a draft-only change.");
        restored.Click("ApplyButton");
        Check(UserInjection.Read().Overrides["animation.fastDurationScale"].GetDouble() == 1,
            "Suggested value did not reach the real host.");
        restored.Click("ResetAllButton");
        Check(UserInjection.Read().Overrides.Count > 0 && restored.Draft != "{}", "Reset all applied before the Apply button.");
        restored.Click("ApplyButton");
        Check(UserInjection.Read().Overrides.Count == 0 && restored.Rows.Length == 0 && restored.Draft == "{}" &&
            AppTypography.ScaleFactor == c.State.Zoom, "Full reset left overrides, rows or an altered ordinary preference.");
    }

    private static void Conflicts(AppController c)
    {
        Set(c, ("appearance.fontScale", 1.1), ("todo.paste.maxItems", 100));
        using var view = new GoodLockView(c);
        view.Command("todo.paste.maxItems", "ResetCommand");
        const string external = "{\"appearance.fontScale\":1.05,\"todo.paste.maxItems\":100,\"external.keep\":[1,2,3]}";
        File.WriteAllText(UserInjection.FilePath, external);
        using (var dialogs = new FixtureDialogs())
        {
            view.Click("ApplyButton");
            Check(dialogs.Count == 1, "Conflict was silently accepted or not reported to the user.");
        }
        Check(File.ReadAllText(UserInjection.FilePath) == external && view.Draft != "{}" && AppTypography.ScaleFactor == 1.1,
            "Conflict changed external bytes, lost the user's draft or adopted unaccepted settings.");
        view.Click("ApplyButton");
        var accepted = UserInjection.Read();
        Check(!accepted.Overrides.ContainsKey("todo.paste.maxItems") && accepted.Overrides.ContainsKey("external.keep") &&
            AppTypography.ScaleFactor == 1.05 && view.Draft == "{}", "Explicit conflict retry did not combine the fresh file with sparse local edits.");
    }

    private static async Task Lifetime(AppController c)
    {
        var active = true;
        using var host = new PaperBodyPluginHostApi(c, c.PaperCommands, "note", "tests.injection-system",
            [PaperTodoPermissionNames.SettingsRead, PaperTodoPermissionNames.SettingsUpdate], () => active, () => active);
        var api = (IPaperInjectionApi)host;
        var snapshot = await Task.Run(api.Read);
        await Task.Run(() => api.Apply(new PaperInjectionPatch
        {
            ExpectedRevision = snapshot.Revision, Set = new Dictionary<string, JsonElement> { ["app.saveIdleMs"] = J(2345d) }
        }));
        Check(Field<DispatcherTimer>(c, "_saveTimer").Interval.TotalMilliseconds == 2345,
            "Worker-thread plugin Apply did not marshal to the real timer owner.");
        using var runtime = new PaperPluginRuntimeWorkspaceApi(c, "tests.injection-runtime",
            [PaperTodoPermissionNames.SettingsRead, PaperTodoPermissionNames.SettingsUpdate], () => active);
        var runtimeApi = (IPaperInjectionApi)runtime;
        await Task.Run(() => runtimeApi.Apply(new PaperInjectionPatch { ExpectedRevision = runtimeApi.Read().Revision, Remove = ["app.saveIdleMs"] }));
        Check(Field<DispatcherTimer>(c, "_saveTimer").Interval.TotalMilliseconds == 1000,
            "Provider Runtime did not use the same removal path.");
        active = false;
        Error<PaperTodoPluginException>(() => api.Read(), "session_closed");
        Error<PaperTodoPluginException>(() => api.Apply(), "session_closed");
        Error<PaperTodoPluginException>(() => api.Restart(), "session_closed");
        Error<PaperTodoPluginException>(() => runtimeApi.Apply(), "runtime_closed");
    }

    private static async Task RestartCancelled(AppController c)
    {
        typeof(AppController).GetField("_ignoreSaveFailures", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c, true);
        c.SaveNow(sync: true);
        using (var locked = new FileStream(Path.Combine(AppContext.BaseDirectory, "data.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            c.RestartForUserInjection("note");
            await Settle();
            var requested = (bool)typeof(App).GetField("_restartAfterExitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Application.Current)!;
            Check(c.IsRunning && !requested && Windows(c)["note"].IsVisible,
                "Failed exit save must retain the editor and cancel the queued successor.");
        }
        c.SaveNow(sync: true);
        Check(c.IsRunning, "Recovery save unexpectedly shut down the application.");
    }

    private static void Restart(AppController c)
    {
        Set(c, ("note.editor.indentationSize", 7), ("note.maxTextChars", 120000), ("appearance.fontScale", 1.1));
        var window = Windows(c)["note"];
        var box = Field<MarkdownPaperBodySession>(window, "_markdownBodySession").NoteBox!;
        Check(box.Options.IndentationSize == 4 && PaperWindow.NoteTextMaxLength == 100000,
            "Restart-only values changed before the real restart.");
        box.Text = "unsaved note before restart";
        window.SetCollapsedState(true);
        var view = new GoodLockView(c);
        // Invoke the actual button handler; it must Apply before requesting normal host exit.
        Call(view.Session, "OnRestart", view.View, new RoutedEventArgs());
        Check(c.IsRunning, "Restart disposed its invoking plugin before the call returned.");
        // Normal application Exit owns this fixture's windows and process termination.
    }

    // Only dismiss dialogs owned by this isolated fixture thread. No user desktop window is touched.
    private sealed class FixtureDialogs : IDisposable
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly HashSet<IntPtr> _seen = [];
        public int Count => _seen.Count;
        public FixtureDialogs()
        {
            _timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
            {
                var name = new StringBuilder(64); GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() == "#32770" && _seen.Add(hwnd))
                {
                    Console.WriteLine("FIXTURE_DIALOG " + hwnd);
                    PostMessage(hwnd, 0x0111, new IntPtr(1), IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            _timer.Start();
        }
        public void Dispose() => _timer.Stop();
    }
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowProc callback, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
