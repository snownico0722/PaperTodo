using System;
using System.Collections.Generic;
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
        var before = controller.State.Papers.ToDictionary(
            paper => paper.Id, paper => (paper.X, paper.Y, paper.Width, paper.Height));

        controller.OpenExpandedEditorFor(a);
        var host = a.ExpandedEditorHostWindow
            ?? throw new InvalidOperationException("Editor host did not open");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var hwnd = new WindowInteropHelper(host).Handle;
        Check(hwnd != IntPtr.Zero && host.IsVisible, "No visible taskbar editor window");
        Check(a.IsExpandedEditorHosted && !a.IsVisible, "The original paper remained visible alongside the editor");
        a.SetExpandedEditorSidebarOpen(true);
        host.UpdateLayout();
        var livePaperList = (ListBox)Member(host, "_editorPaperList");
        var paperB = controller.State.Papers.Single(p => p.Id == "editor-b");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = true;
        a.RefreshEffectiveTopmost();
        Check(host.Topmost, "Pin button only modified the hidden PaperWindow");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = false;
        a.RefreshEffectiveTopmost();
        Check(!host.Topmost, "Unpin did not update the visible editor host");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = true;
        a.RefreshEffectiveTopmost();

        // Route the same sidebar button click a user makes, starting with keyboard
        // focus on that button; activation alone is not proof of text input ownership.
        var secondItem = Descendants<Button>(livePaperList).First(item =>
            Descendants<TextBlock>(item).Any(label => label.Text == "Second"));
        secondItem.Focus();
        secondItem.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var secondEditor = (MarkdownTextBox)Member(b, "_noteBox");
        Check(secondEditor.IsKeyboardFocusWithin,
            "Sidebar switch did not transfer keyboard focus to the new document");
        var focused = System.Windows.Input.Keyboard.FocusedElement as UIElement
            ?? throw new InvalidOperationException("Sidebar switch left no keyboard target");
        var beforeInput = secondEditor.Text;
        var input = new System.Windows.Input.TextCompositionEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, focused, "A中文"))
        { RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent };
        focused.RaiseEvent(input);
        Check(secondEditor.Text.Contains("A中文") &&
              ((MarkdownTextBox)Member(a, "_noteBox")).Text == "# First\nOriginal editor content",
            "Text input after sidebar switch did not reach only the new document");
        secondEditor.Undo();
        Check(secondEditor.Text == beforeInput, "Undo after switching targeted the wrong document");
        Check(ReferenceEquals(host, b.ExpandedEditorHostWindow), "Sidebar replaced the editor host");
        Check(new WindowInteropHelper(host).Handle == hwnd, "Switching papers changed the editor HWND");
        Check(host.CurrentPaperId == "editor-b", "Sidebar did not select the second paper");
        Check(a.Topmost, "Collapsing the prior paper lost its pin state");
        Check(controller.State.Papers.Single(p => p.Id == "editor-a").IsCollapsed && a.IsVisible,
            "Switching away from an expanded paper did not collapse its desktop surface");
        Check(secondEditor.ShowLineNumbers && !((MarkdownTextBox)Member(a, "_noteBox")).ShowLineNumbers,
            "Line numbers did not follow the active editor presentation");
        controller.State.Papers.Single(p => p.Id == "editor-a").AlwaysOnTop = false;
        a.RefreshEffectiveTopmost();
        host.WindowState = WindowState.Minimized;
        Check(controller.ApplyPaperPresentation(paperB, PaperPresentationAction.Activate, true) &&
              host.WindowState != WindowState.Minimized &&
              ReferenceEquals(b.ExpandedEditorHostWindow, host),
            "Plugin Activate did not restore and focus the editor's real window");
        Check(host.IsActive && !a.IsActive,
            "Sidebar switch incorrectly activated the previous desktop paper");

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
              hostedShield.Visibility == Visibility.Collapsed,
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
        // A normal Ctrl+F, context-menu command, or keyboard input must still be owned
        // by B's original PaperWindow even though its visual now lives in the editor host.
        secondEditor.Text = "# Changed from the shared editor";
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
              todoShield.Visibility == Visibility.Collapsed,
            "Switching papers under global lock did not restore desktop protection and free the editor");
        Check(!b.IsExpandedEditorHosted && controller.State.Papers.Single(p => p.Id == "editor-b").IsCollapsed,
            "Leaving B did not restore its desktop capsule state");

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
        var close = (Button)Member(a, "_closeButton");
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

        // The editor keeps edge access even when ordinary expanded desktop papers hide it.
        controller.State.UseDeepCapsuleMode = true;
        controller.State.ShowDeepCapsuleWhileExpanded = false;
        controller.ArrangeDeepCapsules(animate: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(a.IsDeepCapsuleSlotVisible, "Edge capsule scenario did not start with a visible capsule");
        controller.OpenExpandedEditorFor(a);
        var edgeHost = a.ExpandedEditorHostWindow!;
        controller.ArrangeDeepCapsules(animate: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(a.IsDeepCapsuleSlotVisible && !a.IsVisible,
            "Opening or rearranging the editor removed its edge capsule");
        edgeHost.WindowState = WindowState.Minimized;
        var capsulePoint = new DeviceScreenPoint(1, 100);
        typeof(PaperWindow).GetMethod("OnEdgeCapsulePointerPressed", Private)!.Invoke(a, [capsulePoint]);
        typeof(PaperWindow).GetMethod("OnEdgeCapsulePointerReleased", Private)!.Invoke(a, [capsulePoint]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(edgeHost.WindowState != WindowState.Minimized && edgeHost.IsActive && !a.IsVisible &&
              ((MarkdownTextBox)Member(a, "_noteBox")).IsKeyboardFocusWithin &&
              WindowNative.KeyboardFocusWindow == new WindowInteropHelper(edgeHost).Handle,
            "Clicking the retained capsule did not restore editor input focus");
        edgeHost.WindowState = WindowState.Minimized;
        a.ActivateFromEdgeShortcut();
        Check(edgeHost.WindowState != WindowState.Minimized && !a.IsVisible,
            "Edge shortcut exposed the hidden desktop owner");
        controller.SwitchExpandedEditorPaper(a, "editor-b");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(collapsedPaper.IsCollapsed && a.IsDeepCapsuleSlotVisible && b.IsDeepCapsuleSlotVisible,
            "Switching the editor lost the previous or current edge capsule");
        edgeHost.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        for (var i = 0; i < 200; i++)
            controller.State.Papers.Add(new PaperData
            {
                Id = "list-" + i, Type = PaperTypes.Note, Title = "List note " + i, IsVisible = false
            });
        controller.OpenExpandedEditorFor(a);
        controller.OpenExpandedEditorFor(b);
        var listHost = a.ExpandedEditorHostWindow!;
        var otherHost = b.ExpandedEditorHostWindow!;
        a.SetExpandedEditorSidebarOpen(true);
        b.SetExpandedEditorSidebarOpen(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = (ListBox)Member(listHost, "_editorPaperList");
        Check(!ReferenceEquals(list, Member(otherHost, "_editorPaperList")),
            "Independent editor windows shared the same sidebar");
        list.ScrollIntoView(list.Items[150]);
        listHost.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var scroll = Descendants<ScrollViewer>(list).First();
        var offset = scroll.VerticalOffset;
        Check(offset > 0 && Descendants<Button>(list).Count() < 100,
            "Long paper list created every row instead of virtualizing the viewport");
        var visibleRow = Descendants<Button>(list).First(item => item.IsVisible);
        visibleRow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        list = (ListBox)Member(listHost, "_editorPaperList");
        scroll = Descendants<ScrollViewer>(list).First();
        Check(listHost.CurrentPaperId!.StartsWith("list-") && Math.Abs(scroll.VerticalOffset - offset) < 1,
            "Switching a paper reset the sidebar scroll position");
        Check(otherHost.CurrentPaperId == "editor-b", "One editor's navigation changed another editor");
    }

    private static void SetGlobalPaperLock(AppController controller, bool locked)
    {
        var method = typeof(AppController).GetMethod("SetAdvancedAllPapersLocked", Private)
            ?? throw new InvalidOperationException("Global paper lock handler missing");
        method.Invoke(controller, [locked]);
    }

    private static object Member(object owner, string name) =>
        owner.GetType().GetField(name, Private)?.GetValue(owner)
        ?? owner.GetType().GetProperty(name, Private)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing field/property: " + name);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
