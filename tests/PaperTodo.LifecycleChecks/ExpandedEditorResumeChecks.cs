using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static class ExpandedEditorResumeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        CheckTextStatistics();

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

        // The statistics UI must not become a second input owner or consume selection.
        bBox.Text = "**中文** [abc](https://example.com)\n\n末尾";
        bBox.Select(bBox.Text.IndexOf("https://", StringComparison.Ordinal), "https://example.com".Length);
        await WaitForCount(statistics, Strings.Format("EditorSelectionCount", 7, 0));
        host.Activate();
        bBox.Focus();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var focus = Keyboard.FocusedElement;
        Check(bBox.IsKeyboardFocusWithin, "Statistics scenario did not begin with editor focus");
        var selection = (bBox.SelectionStart, bBox.SelectionLength);
        var trigger = (Button)Member(b, "_editorStatisticsButton");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var popup = (Popup)Member(b, "_editorStatisticsPopup");
        Check(popup.IsOpen && ReferenceEquals(focus, Keyboard.FocusedElement) &&
              selection == (bBox.SelectionStart, bBox.SelectionLength),
            "Opening statistics stole editor focus or changed its selection");
        var totals = (TextBlock[])Member(b, "_editorStatisticsTotals");
        var selected = (TextBlock[])Member(b, "_editorStatisticsSelected");
        Check(totals[0].Text == "3" && totals[1].Text == "7" && selected[1].Text == "0" &&
              selected[2].Text == "19" && selected.All(label => label.Visibility == Visibility.Visible),
            "Popup did not show document/selection statistics with shared Markdown semantics");
        var shell = (UIElement)Member(b, "_shell");
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host),
            Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        shell.RaiseEvent(escape);
        Check(escape.Handled && !popup.IsOpen && selection == (bBox.SelectionStart, bBox.SelectionLength) &&
              ReferenceEquals(focus, Keyboard.FocusedElement), "Escape did not dismiss statistics without moving input");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!popup.IsOpen, "Second statistics click did not toggle the popup closed");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Check(!popup.IsOpen, "Clicking outside statistics did not close it");
        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bBox.Select(0, 0);
        await WaitForCount(statistics, Strings.Format("EditorCharacterCount", 7));
        Check(selected.All(label => label.Visibility == Visibility.Collapsed),
            "Clearing selection left the selection column visible");

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
        Check(!popup.IsOpen, "Switching papers left the old statistics popup open");
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
    }

    private static void CheckTextStatistics()
    {
        Total("", new(1, 0, 0));
        Total("中😀 \r\n\t", new(2, 2, 2));
        Total("*中* [a](u)", new(1, 2, 9));
        Total("a\r\n\r\nb\n", new(4, 2, 2));
        Body("# 标题\n**中文** [abc](https://example.com)\n![](i:1)", 7);
        Body("中文 abc😀\n\t", 6);
        Body("![image|100%](i:123)", 5);
        Body("![image|50%](i:123){width=75%}", 5);
        Body("![image|100%](i:123)\n![second|30%](i:456){width=75%}", 11);
        Body("标题\n===\n\n- [x] 甲\n> 乙\n\n---", 4);
        Body("\\*a\\* &amp; <b>中</b>", 5);
        Body("&NotEqualTilde;", 2);
        Body("<div>foo</div>", 14);
        Body("abc **bad", 8);
        Body("[label][ref]\n\n[ref]: https://example.com", 5);
        Body("<https://a>", 9);
        Body("```cs\n**x**\n```", 5);
        Body("```cs\n**x**", 5);
        Body("> ```\n> **x**\n> ```", 5);
        Body("    **x**\n\t**y**", 10);
        Body("`a **b**`", 6);
        Body("```\n![image|50%](i:1)\n```", 17);

        const string markdown = "**中文** [abc](https://example.com)\n![image|50%](i:123){width=75%}\n```\n**x**\n```";
        var counts = NoteTextStatistics.Create(markdown);
        foreach (var (fragment, expected) in new[]
        {
            ("中文", 2), ("**", 0), ("abc", 3), ("https://example.com", 0),
            ("image", 5), ("|50%", 0), ("i:123", 0), ("{width=75%}", 0), ("**x**", 5)
        })
            Equal(expected, counts.Select(markdown.IndexOf(fragment, StringComparison.Ordinal), fragment.Length).Body,
                "Selected " + fragment);
        Equal(counts.Total, counts.Select(0, markdown.Length), "Whole selection");
        Equal(default(NoteTextCounts), counts.Select(2, 0), "No selection");
        var lines = NoteTextStatistics.Create("a\r\n\r\nb\n");
        Equal(new NoteTextCounts(1, 1, 1), lines.Select(0, 3), "Selected trailing CRLF");
        Equal(new NoteTextCounts(1, 0, 0), lines.Select(3, 2), "Selected empty line");
        Equal(new NoteTextCounts(1, 1, 1), lines.Select(5, 1), "Selected final text line");
        Equal(lines.Total, lines.Select(0, 7), "Whole selection with trailing newline");
    }

    private static void Total(string text, NoteTextCounts expected)
        => Equal(expected, NoteTextStatistics.Create(text).Total, text);
    private static void Body(string text, int expected)
        => Equal(expected, NoteTextStatistics.Count(text), text);
    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}; got {actual}");
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
