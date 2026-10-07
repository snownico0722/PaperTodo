using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, ".normal-use-fixture")))
            throw new InvalidOperationException("Not an isolated benchmark directory.");
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Run(int.Parse(args[0]), args[1] == "324"); }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static async Task Run(int count, bool linked)
    {
        var area = SystemParameters.WorkArea;
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = false,
            UseDeepCapsuleMode = false, EnableTodoPaperLinks = true, ShowTodoBottomBar = false,
            ShowLinkedPaperName = false, AutoClearCompletedTodos = false,
            AutoMoveCompletedTodosToBottom = false, ExperimentalEdgeCapsuleHoverPreview = false,
            UsePersistentPowerShellProcess = false, McpEnabled = false,
            FullscreenTopmostMode = FullscreenTopmostModes.StayOnTop,
            Theme = "light", PaperSkin = PaperSkins.Paper
        };
        var data = new PaperData
        {
            Id = "normal", Type = PaperTypes.Todo, IsVisible = true, IsCollapsed = false,
            X = area.Left + 50, Y = area.Top + 50, Width = 450, Height = 520, AlwaysOnTop = true,
            Items = Enumerable.Range(0, count).Select(i => new PaperItem
            {
                Id = "row-" + i, Order = i, Text = "Normal todo " + i
            }).ToList()
        };
        state.Papers.Add(data);
        if (linked)
        {
            state.Papers.Add(new PaperData
            {
                Id = "target", Type = PaperTypes.Note, Content = "target", IsVisible = true,
                IsCollapsed = false, X = area.Left + 520, Y = area.Top + 50, Width = 300, Height = 260
            });
            foreach (var item in data.Items) item.LinkPaper("target");
        }
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        var watch = Stopwatch.StartNew();
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var window = windows["normal"];
        await Idle();
        Require(window.HasExpandedPaperSurface && window.ActualWidth > 100, "paper not ready");
        var startupMs = watch.Elapsed.TotalMilliseconds;
        var paper = controller.State.Papers.Single(p => p.Id == "normal");
        var editor = Editors(window)["row-0"];
        LifecycleInput.Focus(window, editor);
        await Idle();
        var point = editor.PointToScreen(new Point(20, editor.ActualHeight / 2));
        LifecycleInput.MoveCursor(point);
        await Idle();
        Require(LifecycleInput.IsMouseTarget(window, point), "right-click target is obscured");
        watch.Restart();
        LifecycleInput.RightClick();
        await Until(() => editor.ContextMenu?.IsOpen == true);
        await Idle();
        var firstRightClickMs = watch.Elapsed.TotalMilliseconds;
        editor.ContextMenu!.IsOpen = false;
        await Idle();
        var oldEditors = Editors(window).ToDictionary(x => x.Key, x => x.Value);
        var reconcile = typeof(PaperWindow).GetMethod("ReconcileTodoRows", Private)!;
        var placement = Enum.Parse(reconcile.GetParameters()[2].ParameterType, "End");
        var visibility = typeof(AppController).GetMethod("RefreshAllTodoRowsForPaperVisibility", Private)!;
        void Refresh()
        {
            if (linked) visibility.Invoke(controller, null);
            else reconcile.Invoke(window, [null, null, placement]);
        }
        Refresh(); await Idle();
        var refreshMs = new List<double>();
        var refreshBytes = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart(); Refresh(); await Idle();
            refreshMs.Add(watch.Elapsed.TotalMilliseconds);
            refreshBytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        var retained = oldEditors.Count(x => ReferenceEquals(x.Value, Editors(window)[x.Key]));
        watch.Restart();
        controller.HideAllPapers(); await Idle();
        controller.ShowAllPapers(); await Idle();
        var hideShowMs = watch.Elapsed.TotalMilliseconds;
        Require(window.HasExpandedPaperSurface, "show-all failed to restore paper");
        double? reorderMs = null, undoMs = null;
        if (count > 1)
        {
            var move = typeof(PaperWindow).GetMethod("MoveItems", Private)!;
            var after = Enum.Parse(move.GetParameters()[2].ParameterType, "After");
            watch.Restart();
            move.Invoke(window, [new[] { "row-0" }, "row-" + (count - 1), after, "row-0"]);
            await Idle(); reorderMs = watch.Elapsed.TotalMilliseconds;
            Require(paper.Items[^1].Id == "row-0", "move did not reorder");
            LifecycleInput.Focus(window, Editors(window)["row-0"]);
            await Idle();
            watch.Restart(); LifecycleInput.KeyChord(0x11, 0x5A);
            await Until(() => paper.Items[0].Id == "row-0");
            await Idle(); undoMs = watch.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine("NORMAL_AUDIT " + JsonSerializer.Serialize(new
        {
            count, linked, startupMs, firstRightClickMs, hideShowMs, reorderMs, undoMs,
            refreshMs, refreshBytes, retainedEditors = retained
        }));
    }
    private static Dictionary<string, TodoTextBox> Editors(PaperWindow window) =>
        (Dictionary<string, TodoTextBox>)Field(window, "_todoEditors");
    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Native input was not processed.");
            await Task.Delay(1);
        }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
