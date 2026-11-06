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
        Check(NoteTextStatistics.Count("![image|100%](i:123)") == 5 &&
              NoteTextStatistics.Count("![image|50%](i:123){width=75%}") == 5,
            "Changing only image display size changed the character count");

        var area = SystemParameters.WorkArea;
        var saved = new ExpandedEditorState
        {
            X = area.Left + 20, Y = area.Top + 20,
            Width = Math.Min(800, area.Width - 40), Height = Math.Min(600, area.Height - 40),
            SidebarOpen = true
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
        Check(a.IsExpandedEditorSidebarOpen, "Saved sidebar visibility was lost");

        // Reading alone never dirties the workspace, and saves contain no document offsets.
        controller.SaveNow(sync: true);
        var dirtyBefore = Member(controller, "_hasPendingDirty");
        box.Select(610, 5);
        box.ScrollToVerticalOffset(720);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(Equals(dirtyBefore, Member(controller, "_hasPendingDirty")), "Reading dirtied the workspace");
        controller.SaveNow(sync: true);
        Check(!store.SerializeState(controller.State).Contains("notePosition", StringComparison.OrdinalIgnoreCase),
            "Saving retained retired document position state");

        controller.SwitchExpandedEditorPaper(a, "resume-b");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var bBox = (MarkdownTextBox)Member(b, "_noteBox");
        var statistics = (TextBlock)Member(b, "_editorStatisticsText");
        await WaitForCount(statistics, Strings.Format("EditorCharacterCount", 6));
        bBox.Select(0, 2);
        await WaitForCount(statistics, Strings.Format("EditorSelectionCount", 6, 2));
        bBox.Select(0, 0);
        bBox.AppendText("!");
        await WaitForCount(statistics, Strings.Format("EditorCharacterCount", 7));

        bBox.Text = "苹果\n香蕉\n梨";
        bBox.Select(0, bBox.Text.Length);
        bBox.InsertLinePrefix("- ");
        Check(bBox.Text == "- 苹果\n- 香蕉\n- 梨" &&
              bBox.SelectionLength == bBox.Text.Length,
            "Multi-line bullet command did not prefix and retain all selected lines");
        bBox.Document.UndoStack.Undo();
        Check(bBox.Text == "苹果\n香蕉\n梨",
            "Multi-line bullet command did not undo in one step");
        bBox.Text = "甲\n乙\n丙";
        bBox.Select(0, "甲\n乙\n".Length);
        bBox.InsertLinePrefix("1. ");
        Check(bBox.Text == "1. 甲\n2. 乙\n丙",
            "Numbering included the line after a trailing selection newline");
        bBox.Select(0, bBox.Text.Length);
        bBox.InsertLinePrefix("> ");
        Check(bBox.Text == "> 1. 甲\n> 2. 乙\n> 丙",
            "Multi-line quote command did not cover all selected lines");

        foreach (var (text, start, length, expected) in new[]
        {
            ("前文示例后文", 2, 2, "前文\n```\n示例\n```\n后文"),
            ("甲\r\n乙\r\n丙", 3, 3, "甲\r\n```\r\n乙\r\n```\r\n丙"),
            ("前后", 1, 0, "前\n```\n\n```\n后"),
            ("```", 0, 3, "````\n```\n````")
        })
        {
            bBox.Text = text;
            bBox.Select(start, length);
            bBox.InsertCodeBlock();
            Check(bBox.Text == expected, "Code fence was not placed on complete lines");
            bBox.Document.UndoStack.Undo();
            Check(bBox.Text == text, "Code block did not undo in one step");
        }

        controller.SwitchExpandedEditorPaper(b, "resume-a");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(host, a.ExpandedEditorHostWindow), "Switching replaced the editor host");
        box.Text = "甲乙丙丁";
        box.Select(1, 1);
        host.ReturnToPaper();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        box.Document.Insert(0, "新");
        box.Select(0, 0);
        controller.OpenExpandedEditorFor(a);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        host = a.ExpandedEditorHostWindow!;
        Check(box.SelectionLength == 0, "Reopening restored a stale document selection");
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

    private static async Task WaitForCount(TextBlock label, string expected)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (label.Text != expected)
        {
            if (timeout.Elapsed.TotalSeconds > 5)
                throw new InvalidOperationException($"Statistics did not update: {label.Text}; expected {expected}");
            await Task.Delay(25);
        }
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 2;
    private static object Member(object owner, string name) =>
        owner.GetType().GetField(name, Private)?.GetValue(owner) ??
        owner.GetType().GetProperty(name, Private)?.GetValue(owner) ??
        throw new InvalidOperationException("Missing member: " + name);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
