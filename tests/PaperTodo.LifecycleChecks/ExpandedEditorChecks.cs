using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PaperTodo;

internal static class ExpandedEditorChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        var state = new AppState
        {
            TelemetryEnabled = false,
            EnableAnimations = false,
            UseCapsuleMode = true,
            UseDeepCapsuleMode = false,
            McpEnabled = false,
            UsePersistentPowerShellProcess = false,
            Papers =
            [
                new() { Id = "editor-a", Type = PaperTypes.Note, Title = "First",
                    Content = "# First\nOriginal editor content", IsVisible = true,
                    X = 120, Y = 90, Width = 330, Height = 280 },
                new() { Id = "editor-b", Type = PaperTypes.Note, Title = "Second",
                    Content = "# Second\nDifferent editor", IsVisible = true,
                    IsCollapsed = true, X = 470, Y = 95, Width = 370, Height = 310 },
                new() { Id = "editor-todo", Type = PaperTypes.Todo, Title = "Tasks",
                    IsVisible = false, X = 250, Y = 460, Width = 320, Height = 290,
                    Items = [new() { Id = "task-a", Text = "Keep the same HWND" }] }
            ]
        };
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var windows = (Dictionary<string, PaperWindow>)Member(controller, "_windows");
        var a = windows["editor-a"];
        var b = windows["editor-b"];
        var firstEditor = Member(a, "_noteBox");
        var secondEditor = Member(b, "_noteBox");
        var firstSession = Member(a, "_markdownBodySession");
        var secondSession = Member(b, "_markdownBodySession");
        var before = controller.State.Papers.ToDictionary(
            paper => paper.Id, paper => (paper.X, paper.Y, paper.Width, paper.Height));

        controller.OpenExpandedEditorFor(a);
        var host = a.ExpandedEditorHostWindow
            ?? throw new InvalidOperationException("Editor host did not open");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var hwnd = new WindowInteropHelper(host).Handle;
        Check(hwnd != IntPtr.Zero && host.IsVisible, "No visible taskbar editor window");
        Check(a.IsExpandedEditorHosted && !a.IsVisible, "The original paper remained visible alongside the editor");
        Check(ReferenceEquals(firstEditor, Member(a, "_noteBox")), "The first Markdown control was duplicated");
        a.SetExpandedEditorSidebarOpen(true);

        controller.SwitchExpandedEditorPaper(a, "editor-b");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(host, b.ExpandedEditorHostWindow), "Sidebar replaced the editor host");
        Check(new WindowInteropHelper(host).Handle == hwnd, "Switching papers changed the editor HWND");
        Check(host.CurrentPaperId == "editor-b", "Sidebar did not select the second paper");
        Check(b.IsExpandedEditorSidebarOpen && !b.IsCollapsed,
            "Switch did not preserve sidebar presentation or expand the borrowed capsule");
        Check(ReferenceEquals(secondEditor, Member(b, "_noteBox")) &&
              ReferenceEquals(secondSession, Member(b, "_markdownBodySession")),
              "The second paper recreated its Markdown editor/session");

        // A normal Ctrl+F, context-menu command, or keyboard input must still be owned
        // by B's original PaperWindow even though its visual now lives in the editor host.
        ((MarkdownTextBox)secondEditor).Text = "# Changed from the shared editor";
        controller.CommitPendingMarkdownContentsForSave();
        Check(controller.State.Papers.Single(p => p.Id == "editor-b").Content ==
              "# Changed from the shared editor", "Editor changes did not reach the authoritative paper");

        controller.SwitchExpandedEditorPaper(b, "editor-todo");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var todo = windows["editor-todo"];
        Check(host.CurrentPaperId == "editor-todo" && todo.IsExpandedEditorHosted,
            "Todo visual did not replace the Markdown visual");
        Check(new WindowInteropHelper(host).Handle == hwnd, "Todo switch changed the WPF HWND");
        Check(!b.IsExpandedEditorHosted && b.IsCollapsed &&
              ReferenceEquals(secondSession, Member(b, "_markdownBodySession")),
              "Leaving B disposed its original editor session");

        host.ReturnToPaper();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!todo.IsExpandedEditorHosted && controller.State.Papers.Single(p => p.Id == "editor-todo").IsVisible,
            "Return did not restore the selected paper as an ordinary paper");

        controller.OpenExpandedEditorFor(b);
        var secondHost = b.ExpandedEditorHostWindow!;
        secondHost.CloseCurrentPaper();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!controller.State.Papers.Single(p => p.Id == "editor-b").IsVisible &&
              !b.IsExpandedEditorHosted, "Closing editor did not close the paper presentation");

        foreach (var p in controller.State.Papers)
            Check(before[p.Id] == (p.X, p.Y, p.Width, p.Height),
                "Editor switch wrote temporary dimensions into the desktop paper: " + p.Id);
        Check(ReferenceEquals(firstSession, Member(a, "_markdownBodySession")),
            "Switching or closing replaced the initial editor session");
        controller.Exit();
    }

    private static object Member(object owner, string name) =>
        owner.GetType().GetField(name, Private)?.GetValue(owner)
        ?? owner.GetType().GetProperty(name, Private)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing field/property: " + name);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
