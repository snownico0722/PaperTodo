using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static class ExpandedEditorResumeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        Check(NoteTextStatistics.Count("# 标题\n**中文** [abc](https://example.com)\n![](i:1)") == 7,
            "Statistics counted Markdown syntax, image URI or link destination");
        Check(NoteTextStatistics.Count("中文 abc😀\n\t") == 6,
            "Statistics counted whitespace or counted a surrogate pair twice");

        var area = SystemParameters.WorkArea;
        var saved = new ExpandedEditorState
        {
            X = area.Left + 20, Y = area.Top + 20,
            Width = Math.Min(800, area.Width - 40), Height = Math.Min(600, area.Height - 40),
            SidebarOpen = true,
            NotePosition = new NoteEditorPosition
            {
                Caret = 320, SelectionStart = 314, SelectionLength = 6, VerticalOffset = 500
            }
        };
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = false,
            UseDeepCapsuleMode = false, McpEnabled = false, UsePersistentPowerShellProcess = false,
            Papers =
            [
                new() { Id = "resume-a", Type = PaperTypes.Note, IsVisible = true,
                    Content = string.Join("\n", Enumerable.Range(1, 150).Select(i => $"Line {i}: resume this paragraph.")),
                    X = 100, Y = 100, Width = 330, Height = 280, ExpandedEditor = saved },
                new() { Id = "resume-b", Type = PaperTypes.Note, IsVisible = true,
                    Content = "中文 abc😀", X = 450, Y = 100, Width = 330, Height = 280 }
            ]
        };
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var windows = (Dictionary<string, PaperWindow>)Member(controller, "_windows");
        var a = windows["resume-a"];
        var b = windows["resume-b"];
        var desktop = controller.State.Papers.ToDictionary(p => p.Id, p => (p.X, p.Y, p.Width, p.Height));

        controller.OpenExpandedEditorFor(a);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var host = a.ExpandedEditorHostWindow!;
        var box = (MarkdownTextBox)Member(a, "_noteBox");
        Check(Near(host.Left, saved.X) && Near(host.Top, saved.Y) &&
              Near(host.Width, saved.Width) && Near(host.Height, saved.Height),
            "Editor did not restore its saved window bounds from disk");
        Check(a.IsExpandedEditorSidebarOpen && box.CaretOffset == 320 &&
              box.SelectionStart == 314 && box.SelectionLength == 6 && Near(box.VerticalOffset, 500),
            "Editor did not restore saved sidebar, caret, selection or viewport");

        // Saving while open covers automatic save, workspace switching and orderly exit.
        box.Select(610, 5);
        box.CaretOffset = 615;
        box.ScrollToVerticalOffset(720);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        controller.SaveNow(sync: true);
        var persisted = new StateStore().Load().Papers.Single(p => p.Id == "resume-a").ExpandedEditor!;
        Check(persisted.NotePosition is { Caret: 615, SelectionStart: 610, SelectionLength: 5 } &&
              Near(persisted.NotePosition.VerticalOffset, box.VerticalOffset),
            "Saving an open editor did not persist its current document position");

        controller.SwitchExpandedEditorPaper(a, "resume-b");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var bBox = (MarkdownTextBox)Member(b, "_noteBox");
        var statistics = (TextBlock)Member(b, "_editorStatisticsText");
        Check(statistics.Text == Strings.Format("EditorCharacterCount", 6), "Wrong total character count");
        bBox.Select(0, 2);
        await Task.Delay(180);
        Check(statistics.Text == Strings.Format("EditorSelectionCount", 6, 2), "Selection count did not update");
        bBox.Select(0, 0);
        bBox.AppendText("!");
        await Task.Delay(180);
        Check(statistics.Text == Strings.Format("EditorCharacterCount", 7),
            "Total count did not update or retained the cleared selection");

        controller.SwitchExpandedEditorPaper(b, "resume-a");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(host, a.ExpandedEditorHostWindow) && box.SelectionStart == 610 &&
              box.SelectionLength == 5 && Near(box.VerticalOffset, persisted.NotePosition!.VerticalOffset),
            "Switching papers lost the previous editing position or replaced the host");
        a.SetExpandedEditorSidebarOpen(false);
        host.WindowState = WindowState.Maximized;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        host.WindowState = WindowState.Minimized;
        controller.SaveNow(sync: true);
        Check(new StateStore().Load().Papers.Single(p => p.Id == "resume-a").ExpandedEditor!.Maximized,
            "Minimizing forgot the last maximized state");
        host.ActivateEditor();
        Check(host.WindowState == WindowState.Maximized, "Activation lost maximization");
        host.Close();
        controller.OpenExpandedEditorFor(a);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(a.ExpandedEditorHostWindow!.WindowState == WindowState.Maximized && !a.IsExpandedEditorSidebarOpen,
            "Closing/reopening lost maximization or sidebar state");

        foreach (var paper in controller.State.Papers)
            Check(desktop[paper.Id] == (paper.X, paper.Y, paper.Width, paper.Height),
                "Editor presentation overwrote desktop paper geometry");
        controller.Exit();
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 2;
    private static object Member(object owner, string name) =>
        owner.GetType().GetField(name, Private)?.GetValue(owner) ??
        throw new InvalidOperationException("Missing field: " + name);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
