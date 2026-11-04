using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Marker = ".papertodo-workbench-fixture";
    private static readonly string[] Cases = ["surface", "workspaces", "readonly"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--fixture", var name] && Cases.Contains(name))
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, Marker))) throw new InvalidOperationException("Not a fixture directory.");
            Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var source = typeof(Program).Assembly.GetManifestResourceStream("PaperTodo.App.xaml")!)
            {
                var xaml = System.Xml.Linq.XDocument.Load(source);
                System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = new System.Xml.Linq.XElement(ns + "ResourceDictionary", xaml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration),
                    xaml.Root.Element(ns + "Application.Resources")!.Nodes());
                app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
            }
            var result = 0;
            app.Dispatcher.InvokeAsync(async () =>
            {
                try { await Run(name); Console.WriteLine("PASS " + name); }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally { app.Shutdown(); }
            });
            app.Run(); return result;
        }
        try
        {
            foreach (var fixtureName in Cases) RunIsolated(fixtureName);
            Console.WriteLine("PASS 3 isolated workbench lifecycle fixtures"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RunIsolated(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperTodo.WorkbenchChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "plugins" or ".artifacts")) continue;
                if (!(Path.GetExtension(file) is ".exe" or ".dll" or ".pdb" || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))) continue;
                var target = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            File.WriteAllText(Path.Combine(root, Marker), "isolated fixture");
            var start = new ProcessStartInfo(Path.Combine(root, "PaperTodo.WorkbenchChecks.exe"))
            { UseShellExecute = false, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(name);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException(name); }
            Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(error.GetAwaiter().GetResult());
            Assert(process.ExitCode == 0, "Fixture failed: " + name);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static AppState FixtureState() => new()
    {
        UiLanguage = "en", TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = true, UseDeepCapsuleMode = true, EnableTodoPaperLinks = true,
        UsePersistentPowerShellProcess = false, McpEnabled = false,
        Workspaces = new() { Enabled = true, Count = 2, Active = 1 },
        Papers = [
            new() { Id = "note-a", Type = PaperTypes.Note, Title = "Research", Content = "# Workbench\nA real editable paper, not a screenshot.\n\n- Strong automatic layout\n- Stable positions\n- Shared editor", X = 100, Y = 80, Width = 360, Height = 400, IsVisible = true, IsCollapsed = false },
            new() { Id = "note-b", Type = PaperTypes.Note, Title = "Ideas", Content = "## Connections\nBuild a continuous desk without a project center.", X = 520, Y = 100, Width = 320, Height = 300, IsVisible = true, IsCollapsed = true },
            new() { Id = "todo-a", Type = PaperTypes.Todo, Title = "Next", X = 900, Y = 100, Width = 280, Height = 340, IsVisible = false,
                Items = [new() { Id = "task-1", Text = "Test editor reuse" }, new() { Id = "task-2", Text = "Keep desktop geometry unchanged" }] }
        ]
    };
    private static async Task Run(string name)
    {
        if (name == "readonly")
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data2.json");
            Assert(StateStore.ReadWorkbenchOverview(path) == null && !File.Exists(path), "Preview initialized missing workspace");
            var original = "{broken-json"; File.WriteAllText(path, original);
            try { StateStore.ReadWorkbenchOverview(path); throw new Exception("Corruption accepted"); } catch (JsonException) { }
            Assert(File.ReadAllText(path) == original && !File.Exists(Path.Combine(AppContext.BaseDirectory, "data2.backup.json")), "Preview modified corrupt data");
            File.WriteAllText(path, "{\"papers\":[]}");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { StateStore.ReadWorkbenchOverview(path); throw new Exception("Occupied file treated as readable/empty"); } catch (IOException) { }
            }
            var state = FixtureState(); state.Workbench = new() { Cards = new() { ["note-a"] = new() { X = 12, Y = 34, Pinned = true } } };
            var store = new StateStore(); store.SaveJsonSync(store.SerializeState(state), 1);
            var read = StateStore.ReadWorkbenchOverview(store.FilePath)!;
            Assert(read.Workbench!.Cards["note-a"].Pinned && read.Papers[0].X == 100, "Spatial metadata did not round-trip independently");
            Assert(!WorkspaceFieldScopes.Inherits(nameof(AppState.Workbench)), "Layout inherits across workspaces");
            return;
        }
        var initial = FixtureState();
        var initialStore = new StateStore(); initialStore.SaveJsonSync(initialStore.SerializeState(initial), 1);
        if (name == "workspaces")
        {
            var other = FixtureState(); other.Workspaces = null; other.Papers = [new() { Id = "other", Type = PaperTypes.Note, Content = "Second workspace", IsVisible = true, X = 100, Y = 100, Width = 320, Height = 360 }];
            var second = new StateStore(AppContext.BaseDirectory, DurableAtomicFileWriter.Shared, 2);
            second.SaveJsonSync(second.SerializeState(other), 1);
        }
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var windows = (Dictionary<string, PaperWindow>)Member(controller, "_windows");
        var masters = (Dictionary<string, MasterCapsuleWindow>)Member(controller, "_masterCapsules");
        Assert(masters.Count > 0 && masters.Values.All(master => master.IsVisible), "Startup master capsule was not visible");
        var originalWindow = controller.WorkbenchPaperWindow("note-a");
        var editor = Member(originalWindow, "_noteBox");
        var bodySession = Member(originalWindow, "_markdownBodySession");
        var before = controller.State.Papers.ToDictionary(p => p.Id, Geometry);
        controller.ToggleWorkbench();
        var workbench = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => workbench.HostedPaperCount == 3 && controller.State.Workbench?.Cards.Count == 3, "Workbench layout did not settle");
        Assert(ReferenceEquals(Member(originalWindow, "_noteBox"), editor), "Workbench duplicated the Markdown editor");
        Assert(ReferenceEquals(Member(originalWindow, "_markdownBodySession"), bodySession), "Workbench duplicated the body session");
        Assert(!originalWindow.IsVisible && originalWindow.IsWorkbenchHosted, "Desktop and workbench body both visible");
        Assert(masters.Values.All(master => !master.IsVisible), "Workbench left a master capsule visible over the desk");
        if (name == "surface")
        {
            // The workbench chrome follows PaperTodo's actual palette, including color-scheme changes.
            var chrome = (Border)Member(workbench, "_toolbarHost");
            Assert(ReferenceEquals(workbench.Background, Theme.CodeBrush) &&
                   ReferenceEquals(chrome.Background, Theme.PaperBrush), "Workbench uses a fixed non-theme palette");
            var originalScheme = controller.State.ColorScheme;
            controller.State.ColorScheme = ColorSchemes.Forest;
            Theme.Invalidate(); workbench.RefreshTheme();
            Assert(ReferenceEquals(workbench.Background, Theme.CodeBrush) &&
                   ReferenceEquals(chrome.Background, Theme.PaperBrush) &&
                   ReferenceEquals(chrome.BorderBrush, Theme.PaperBorderBrush),
                "Workbench chrome did not follow the updated palette");
            controller.State.ColorScheme = originalScheme;
            Theme.Invalidate(); workbench.RefreshTheme();

            var search = (TextBox)Member(workbench, "_search");
            var left = (StackPanel)Member(workbench, "_layoutCommands");
            var right = (StackPanel)Member(workbench, "_rightCommands");
            Assert(Grid.GetColumn(search) == 1 && left.Children.Count == 2 &&
                right.Children.Count == 6, "Workbench toolbar groups are misplaced");

            // A large document and a Todo item after the twelfth preview entry are searchable.
            const string needle = "deep-search-target";
            var searchState = new AppState { Papers =
            [
                new() { Id = "long-note", Type = PaperTypes.Note, Content = new string('x', 900) + needle },
                new() { Id = "long-todo", Type = PaperTypes.Todo,
                    Items = Enumerable.Range(0, 14).Select(i => new PaperItem
                    { Id = "item-" + i, Text = i == 13 ? needle : "ordinary item" }).ToList() }
            ] };
            var searchSnapshot = controller.SnapshotWorkbench(searchState, controller.WorkspaceNumber, live: false);
            var match = typeof(WorkbenchWindow).GetMethod("SearchMatches", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var entry in searchSnapshot.Papers)
                Assert(!entry.Summary.Contains(needle) &&
                    (bool)match.Invoke(null, [entry, needle])!, "Search is still limited to preview content");

            var active = Member(workbench, "ActiveScene");
            var cards = (System.Collections.IDictionary)Member(active, "Cards");
            var card = cards["note-a"]!;
            var zoom = (ScaleTransform)Member(workbench, "_scale");
            var pan = (TranslateTransform)Member(workbench, "_pan");
            var camera = (zoom.ScaleX, pan.X, pan.Y);
            var focus = typeof(WorkbenchWindow).GetMethod("FocusPaper", Private)!;
            var clear = typeof(WorkbenchWindow).GetMethod("ClearPaperFocus", Private)!;
            focus.Invoke(workbench, [card]);
            Assert((string)Member(workbench, "_focusedPaperId") == "note-a" &&
                   zoom.ScaleX > camera.ScaleX, "Click-to-focus camera did not zoom to the paper");
            clear.Invoke(workbench, [true]);
            Assert(Math.Abs(zoom.ScaleX - camera.ScaleX) < 0.001 &&
                   Math.Abs(pan.X - camera.X) < 0.001 && Math.Abs(pan.Y - camera.Y) < 0.001,
                "Outside click did not restore the previous camera");

            // Thumb sends DragStarted/Completed even for a click with zero displacement.
            var undo = (Stack<WorkbenchState>)Member(workbench, "_undo");
            var originalUndoCount = undo.Count;
            var move = (Thumb)Member(card, "Move");
            move.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            move.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Assert(undo.Count == originalUndoCount, "Clicking a title created a layout undo entry");

            workbench.SuspendWorkspace();
            controller.CloseWorkbenchForDesktopAction();
            Assert(controller.HasWorkbench, "Suspended workbench was closed by an internal reload action");
            workbench.ResumeWorkspace();
            await Until(() => workbench.HostedPaperCount == 3, "Workbench failed to resume after reload");
        }
        foreach (var paper in controller.State.Papers) Assert(before[paper.Id] == Geometry(paper), "Desktop state changed while entering: " + paper.Id);
        editor.GetType().GetProperty("Text")!.SetValue(editor, "Edited inside the workbench");
        controller.CommitPendingMarkdownContentsForSave();
        Assert(controller.FindPaper("note-a")!.Content == "Edited inside the workbench", "Workbench edits do not reach the authoritative model");
        var copied = controller.WorkbenchLayout.Copy(); copied.Cards["note-a"].X += 1200; copied.Cards["note-a"].Pinned = true;
        controller.StoreWorkbenchLayout(copied);
        controller.SaveNow(sync: true);
        Assert(controller.FindPaper("note-a")!.X == before["note-a"].X, "Workbench move changed desktop coordinates");
        if (name == "surface")
        {
            SaveImage(workbench, "workbench-active.png");
            controller.ToggleWorkbench();
            Assert(masters.Count > 0 && masters.Values.All(master => master.IsVisible), "Closing workbench did not restore master capsules");
            Assert(!originalWindow.IsWorkbenchHosted, "Lease not returned");
            Assert(ReferenceEquals(Member(originalWindow, "_noteBox"), editor), "Editor replaced on return");
            foreach (var paper in controller.State.Papers) Assert(before[paper.Id] == Geometry(paper), "Desktop state changed on return: " + paper.Id);
            controller.ToggleWorkbench(); workbench = (WorkbenchWindow)Member(controller, "_workbenchWindow");
            await Until(() => workbench.HostedPaperCount == 3, "Second enter failed");
            controller.DeletePaper(controller.FindPaper("note-b")!);
            await Until(() => workbench.HostedPaperCount == 2, "Deleted paper retained a leased editor");
            Assert(!controller.WorkbenchLayout.Cards.ContainsKey("note-b"), "Deleted paper retained spatial metadata");
            // Reuse the native Todo-row association, never a generic paper-to-paper edge.
            Assert(controller.WorkbenchPaperWindow("todo-a").LinkPaperToTodo("task-1", "note-a"),
                "Native Todo-item binding was unavailable in the workbench");
            Assert(controller.FindPaper("todo-a")!.Items.First(i => i.Id == "task-1").LinkedPaperId == "note-a",
                "Binding was not stored on the specific Todo item");
            Assert(controller.SnapshotCurrentWorkbench().Edges.Any(e => e.From == "todo-a" && e.To == "note-a"),
                "Todo-item binding was not reflected by the workbench layout");
            // The eye changes only desktop visibility while the real editor stays hosted.
            controller.SetWorkbenchDesktopVisibility(controller.FindPaper("note-a")!, false);
            controller.SetWorkbenchDesktopVisibility(controller.FindPaper("todo-a")!, true);
            Assert(!controller.FindPaper("note-a")!.IsVisible && controller.FindPaper("todo-a")!.IsVisible,
                "Eye did not change the desktop visibility model");
            Assert(workbench.HostedPaperCount == 2, "Eye visibility detached the workbench editor");
            controller.ToggleWorkbench();
            Assert(!originalWindow.IsVisible, "Hidden-on-desktop paper was shown after closing workbench");
            Assert(windows["todo-a"].IsVisible, "Eye did not show previously hidden paper on desktop");
        }
        else
        {
            var secondPath = Path.Combine(AppContext.BaseDirectory, "data2.json");
            using (new FileStream(secondPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(!await controller.SwitchWorkspaceAsync(2), "Occupied workspace switch succeeded");
            Assert(controller.WorkspaceNumber == 1 && workbench.HostedPaperCount == 3, "Failed switch lost the current desk");
            Assert(await controller.SwitchWorkspaceAsync(2), "Workspace switch failed");
            Assert(masters.Values.All(master => !master.IsVisible), "Workspace switch exposed master capsules over the workbench");
            await Until(() => workbench.HostedPaperCount == 1 && controller.State.Workbench?.Cards.Count == 1, "New workspace editor not mounted");
            Assert(originalWindow.IsClosed && !originalWindow.IsWorkbenchHosted, "Old workspace lease survived teardown");
            Assert(StateStore.ReadWorkbenchOverview(Path.Combine(AppContext.BaseDirectory, "data.json"))!.Papers.First(p => p.Id == "note-a").Content == "Edited inside the workbench", "Switch lost pending edits");
            Assert(await controller.SwitchWorkspaceAsync(1), "Switch back failed");
            await Until(() => workbench.HostedPaperCount == 3, "Original workspace not restored");
            Assert(controller.WorkbenchLayout.Cards["note-a"].Pinned, "Workspace layout was not persisted");
            controller.ToggleWorkbench();
            Assert(masters.Count > 0 && masters.Values.All(master => master.IsVisible), "Leaving switched workbench did not restore master capsules");
        }
        controller.SaveNow(sync: true);
    }
    private static (double X, double Y, double Width, double Height, bool IsVisible, bool IsCollapsed) Geometry(PaperData paper) =>
        (paper.X, paper.Y, paper.Width, paper.Height, paper.IsVisible, paper.IsCollapsed);
    private static object Member(object target, string name) => target.GetType().GetField(name, Private)?.GetValue(target)
        ?? target.GetType().GetProperty(name, Private)?.GetValue(target) ?? throw new MissingMemberException(name);
    private static async Task Until(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message); await Task.Delay(20); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void SaveImage(Window window, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("WORKBENCH_ARTIFACT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, filename)); encoder.Save(stream);
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
