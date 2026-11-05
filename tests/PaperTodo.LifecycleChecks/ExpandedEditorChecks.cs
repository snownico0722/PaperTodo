using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

        var modeButton = (Button)Member(a, "_editorModeButton");
        var originalIcons = (StackPanel)Member(a, "_topBarActionButtonsHost");
        Check(originalIcons.Children.Contains(modeButton) &&
              PaperTopBarShortcuts.IsPinned(controller.State, PaperTopBarShortcuts.ExpandedEditor),
            "Editor mode does not participate in the regular saved shortcuts");
        var originalGeometry = ModeGeometry(modeButton);

        controller.OpenExpandedEditorFor(a);
        var host = a.ExpandedEditorHostWindow
            ?? throw new InvalidOperationException("Editor host did not open");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var hwnd = new WindowInteropHelper(host).Handle;
        Check(hwnd != IntPtr.Zero && host.IsVisible, "No visible taskbar editor window");
        Check(a.IsExpandedEditorHosted && !a.IsVisible, "The original paper remained visible alongside the editor");
        Check(ReferenceEquals(firstEditor, Member(a, "_noteBox")), "The first Markdown control was duplicated");
        Check(!string.Equals(originalGeometry, ModeGeometry(modeButton), StringComparison.Ordinal),
            "Entering editor did not change the centered vector shortcut icon");
        var topBar = (Border)Member(a, "_topBarHost");
        Check(topBar.ContextMenu != null, "Borrowed PaperTodo titlebar lost its right-click menu");
        var windowButtons = (StackPanel)Member(a, "_topBarButtonsHost");
        Check(ReferenceEquals(windowButtons.Children[windowButtons.Children.Count - 1],
            Member(a, "_closeButton")), "Close/capsule button is no longer rightmost");
        var frame = (Border)Member(host, "_frame");
        Check(frame.BorderThickness.Left == 0 && frame.CornerRadius.TopLeft == 12 &&
              frame.Clip is RectangleGeometry, "Expanded editor has a square or extra bordered shell");
        var sidebar = (Border)Member(a, "_editorPaperSidebar");
        a.SetExpandedEditorSidebarOpen(true);
        Check(sidebar.Margin.Top == 0, "Paper list leaves an empty strip for the Markdown toolbar");
        var livePaperList = (StackPanel)Member(a, "_editorPaperList");
        Check(livePaperList.Children.Count == 3, "Initial editor sidebar did not list all papers");
        var createdPaper = controller.CreatePaper(PaperTypes.Note, show: false)
            ?? throw new InvalidOperationException("Could not create an additional note");
        Check(livePaperList.Children.Count == 4,
            "Open editor sidebar did not update after creating another paper");
        var paperB = controller.State.Papers.Single(p => p.Id == "editor-b");
        paperB.Title = "Renamed second paper";
        b.RefreshPaperTitle();
        Check(livePaperList.Children.OfType<Button>().Any(item =>
                ((StackPanel)item.Content).Children.OfType<TextBlock>().First().Text ==
                    "Renamed second paper"),
            "Open editor sidebar did not update after renaming a paper");
        controller.DeletePaper(createdPaper);
        Check(livePaperList.Children.Count == 3,
            "Open editor sidebar did not update after deleting another paper");
        var paperItems = (StackPanel)Member(a, "_editorPaperList");
        var firstItem = (Button)paperItems.Children[0];
        firstItem.ApplyTemplate();
        var selectionBorder = firstItem.Template.FindName("ItemSurface", firstItem) as Border;
        Check(selectionBorder?.CornerRadius.TopLeft == 7,
            "Sidebar selected-row background does not have rounded corners");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = true;
        a.RefreshEffectiveTopmost();
        Check(host.Topmost, "Pin button only modified the hidden PaperWindow");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = false;
        a.RefreshEffectiveTopmost();
        Check(!host.Topmost, "Unpin did not update the visible editor host");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = true;
        a.RefreshEffectiveTopmost();

        controller.SwitchExpandedEditorPaper(a, "editor-b");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(host, b.ExpandedEditorHostWindow), "Sidebar replaced the editor host");
        Check(new WindowInteropHelper(host).Handle == hwnd, "Switching papers changed the editor HWND");
        Check(host.CurrentPaperId == "editor-b", "Sidebar did not select the second paper");
        Check(a.Topmost, "Returning the prior desktop paper lost its pin state");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = false;
        a.RefreshEffectiveTopmost();
        host.WindowState = WindowState.Minimized;
        Check(controller.ApplyPaperPresentation(paperB, PaperPresentationAction.Activate, true) &&
              host.WindowState != WindowState.Minimized &&
              ReferenceEquals(b.ExpandedEditorHostWindow, host),
            "Plugin Activate did not restore and focus the editor's real window");
        Check(host.IsActive && !a.IsActive,
            "Sidebar switch incorrectly activated the previous desktop paper");

        controller.State.ExperimentalHideInactiveTitleBar = true;
        controller.State.ExperimentalHideInactiveTopBarButtons = true;
        b.UpdateExperimentalFocusPresentationSettings();
        Check(((Border)Member(b, "_topBarHost")).IsHitTestVisible &&
              ((StackPanel)Member(b, "_topBarActionButtonsHost")).IsHitTestVisible &&
              ((StackPanel)Member(b, "_topBarActionButtonsHost")).Opacity == 1,
            "Editor inherited desktop inactive titlebar/button hiding");
        controller.State.ExperimentalHideInactiveTitleBar = false;
        controller.State.ExperimentalHideInactiveTopBarButtons = false;
        b.UpdateExperimentalFocusPresentationSettings();

        controller.HideAllPapers();
        Check(host.IsVisible && b.IsExpandedEditorHosted && paperB.IsVisible && !a.IsVisible,
            "Hide All hid the editor or its authoritative paper");
        controller.ExecuteStartupCommand(new StartupCommand(StartupCommandKind.Toggle));
        Check(host.IsVisible && b.IsExpandedEditorHosted && a.IsVisible,
            "Global toggle failed to restore desktop papers independently of the editor");
        controller.ExecuteStartupCommand(new StartupCommand(StartupCommandKind.Toggle));
        Check(host.IsVisible && b.IsExpandedEditorHosted && !a.IsVisible,
            "Global toggle incorrectly included the editor in desktop visibility");
        controller.ShowAllPapers();

        host.WindowState = WindowState.Minimized;
        controller.OpenLinkedPaper("editor-b");
        Check(host.WindowState != WindowState.Minimized && b.IsExpandedEditorHosted,
            "Linked paper navigation targeted the hidden desktop window");
        host.WindowState = WindowState.Minimized;
        Check(ReferenceEquals(controller.OpenBuiltInFindTarget("editor-b"), b) &&
              host.WindowState != WindowState.Minimized,
            "Global find target navigation failed to restore the editor host");

        // Global interaction lock protects desktop papers, never the currently borrowed editor.
        SetGlobalPaperLock(controller, locked: true);
        var desktopShield = (Border)Member(a, "_advancedLockShield");
        var hostedShield = (Border)Member(b, "_advancedLockShield");
        Check(desktopShield.Visibility == Visibility.Visible &&
              hostedShield.Visibility == Visibility.Collapsed &&
              ((Button)Member(b, "_paperIconButton")).Visibility == Visibility.Visible,
            "Desktop lock hid editor controls or failed to protect a desktop paper");
        var editorKey = new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(host),
            Environment.TickCount,
            System.Windows.Input.Key.G)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
        };
        ((UIElement)Member(b, "_shell")).RaiseEvent(editorKey);
        Check(!editorKey.Handled, "Desktop lock intercepted a normal key in the editor");

        Check(b.IsExpandedEditorSidebarOpen && !controller.State.Papers.Single(p => p.Id == "editor-b").IsCollapsed,
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
        var todoShield = (Border)Member(todo, "_advancedLockShield");
        Check(hostedShield.Visibility == Visibility.Visible &&
              desktopShield.Visibility == Visibility.Visible &&
              todoShield.Visibility == Visibility.Collapsed &&
              ((Button)Member(todo, "_paperIconButton")).Visibility == Visibility.Visible,
            "Switching papers under global lock did not restore desktop protection and free the editor");
        Check(!b.IsExpandedEditorHosted && controller.State.Papers.Single(p => p.Id == "editor-b").IsCollapsed &&
              ReferenceEquals(secondSession, Member(b, "_markdownBodySession")),
              "Leaving B disposed its original editor session");

        // The editor's ordered shortcut itself performs return, independently of ×.
        var todoModeButton = (Button)Member(todo, "_editorModeButton");
        todoModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!todo.IsExpandedEditorHosted && controller.State.Papers.Single(p => p.Id == "editor-todo").IsVisible,
            "Return did not restore the selected paper as an ordinary paper");
        Check(todoShield.Visibility == Visibility.Visible && desktopShield.Visibility == Visibility.Visible,
            "Returning from editor did not reapply global desktop lock");
        SetGlobalPaperLock(controller, locked: false);
        Check(todoShield.Visibility == Visibility.Collapsed &&
              hostedShield.Visibility == Visibility.Collapsed &&
              desktopShield.Visibility == Visibility.Collapsed,
            "Unlocking desktop papers failed after an editor handoff");

        controller.OpenExpandedEditorFor(b);
        var pluginHost = b.ExpandedEditorHostWindow!;
        Check(controller.ApplyPaperPresentation(paperB, PaperPresentationAction.Collapse, true) &&
              !b.IsExpandedEditorHosted && !pluginHost.IsVisible && paperB.IsCollapsed,
            "Plugin Collapse left a borrowed body inside the editor HWND");

        controller.OpenExpandedEditorFor(a);
        var secondHost = a.ExpandedEditorHostWindow!;
        var close = (Button)Member(a, "_closeButton");
        Check(Equals(close.ToolTip, Strings.Get("ToolTipCollapseToCapsule")) &&
              close.Content is not string, "Editor × is not the collapse-to-capsule action");
        close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var collapsedPaper = controller.State.Papers.Single(p => p.Id == "editor-a");
        Check(collapsedPaper.IsVisible && collapsedPaper.IsCollapsed &&
              !a.IsExpandedEditorHosted, "Far-right editor × did not collapse the paper to a capsule");

        controller.OpenExpandedEditorFor(todo);
        var systemCloseHost = todo.ExpandedEditorHostWindow!;
        systemCloseHost.Close(); // WPF OnClosing: Alt+F4 / taskbar close
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!todo.IsExpandedEditorHosted && !systemCloseHost.IsVisible &&
              controller.State.Papers.Single(p => p.Id == "editor-todo").IsCollapsed,
            "System Close must complete once and collapse the selected paper");

        foreach (var p in controller.State.Papers)
            Check(before[p.Id] == (p.X, p.Y, p.Width, p.Height),
                "Editor switch wrote temporary dimensions into the desktop paper: " + p.Id);
        Check(ReferenceEquals(firstSession, Member(a, "_markdownBodySession")),
            "Switching or closing replaced the initial editor session");
        controller.Exit();
    }

    private static void SetGlobalPaperLock(AppController controller, bool locked)
    {
        var method = typeof(AppController).GetMethod("SetAdvancedAllPapersLocked", Private)
            ?? throw new InvalidOperationException("Global paper lock handler missing");
        method.Invoke(controller, [locked]);
    }

    private static string ModeGeometry(Button button)
    {
        var icon = (Viewbox)button.Content;
        var canvas = (Canvas)icon.Child;
        var path = (System.Windows.Shapes.Path)canvas.Children[0];
        return path.Data.ToString();
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
